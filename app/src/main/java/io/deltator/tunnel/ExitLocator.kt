package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.ssl.SSLSocketFactory

/** Where the traffic leaves the Tor network. */
data class ExitInfo(
    val ip: String,
    val countryCode: String = "",
    val countryName: String = "",
    val city: String = "",
    val asn: String = "",
    /** True when the country came from ip2location rather than the offline GeoIP db. */
    val fromNetwork: Boolean = false
) {
    /** "BE \u00b7 Belgium" or just the name when the code is unknown. */
    fun label(): String = when {
        countryName.isNotBlank() && countryCode.isNotBlank() -> "$countryCode \u00b7 $countryName"
        countryName.isNotBlank() -> countryName
        countryCode.isNotBlank() -> countryCode
        else -> "unknown"
    }
}

/**
 * Resolves the public exit IP and country as seen through the app's SOCKS5
 * tunnel, i.e. through the Tor circuit rather than the device's own network.
 *
 * Primary source is ip2location.io over TLS; when that is rate limited or
 * unreachable it falls back to a plain-HTTP IP echo plus the bundled offline
 * GeoIP database.
 */
object ExitLocator {
    private const val TAG = "ExitLocator"

    private const val GEO_HOST = "api.ip2location.io"
    private const val GEO_PORT = 443
    private val echoHosts = listOf("api.ipify.org", "icanhazip.com", "ifconfig.me")

    /** Full lookup: country straight from the network, or from the offline db. */
    suspend fun locate(
        context: Context,
        socksHost: String,
        socksPort: Int,
        timeoutMs: Int = 15000
    ): ExitInfo? {
        geoLookup(socksHost, socksPort, timeoutMs)?.let { return it }

        for (host in echoHosts) {
            val ip = try {
                echo(socksHost, socksPort, host, timeoutMs)
            } catch (e: Exception) {
                Log.w(TAG, "Echo probe $host failed: ${e.message}")
                null
            } ?: continue
            val offline = runCatching { BridgeCountries.countryInfo(context, ip) }.getOrNull()
            return ExitInfo(
                ip = ip,
                countryCode = offline?.first ?: "",
                countryName = offline?.second ?: "",
                fromNetwork = false
            )
        }
        return null
    }

    // --- ip2location.io over TLS ---------------------------------------------

    private fun geoLookup(socksHost: String, socksPort: Int, timeoutMs: Int): ExitInfo? = try {
        val raw = httpsGet(socksHost, socksPort, GEO_HOST, GEO_PORT, "/", timeoutMs)
        val body = raw.substringAfter("\r\n\r\n", "")
        val ip = json(body, "ip")
        if (ip.isNullOrBlank()) {
            Log.w(TAG, "ip2location returned no ip: ${body.take(160)}")
            null
        } else {
            ExitInfo(
                ip = ip,
                countryCode = json(body, "country_code").orEmpty(),
                countryName = json(body, "country_name").orEmpty(),
                city = json(body, "city_name").orEmpty(),
                asn = json(body, "as").orEmpty(),
                fromNetwork = true
            )
        }
    } catch (e: Exception) {
        Log.w(TAG, "ip2location lookup failed: ${e.message}")
        null
    }

    private fun json(body: String, key: String): String? =
        Regex("\"" + Regex.escape(key) + "\"\\s*:\\s*\"([^\"]*)\"")
            .find(body)?.groupValues?.get(1)

    // --- plain HTTP echo ------------------------------------------------------

    private fun echo(socksHost: String, socksPort: Int, host: String, timeoutMs: Int): String? {
        val raw = httpGet(socksHost, socksPort, host, 80, "/", timeoutMs)
        val body = raw.substringAfter("\r\n\r\n", "").trim()
        return body.lineSequence().map { it.trim() }.firstOrNull { IPV4.matches(it) }
    }

    private val IPV4 = Regex("^(?:\\d{1,3}\\.){3}\\d{1,3}$")

    // --- SOCKS5 plumbing ------------------------------------------------------

    private fun socksConnect(
        socksHost: String,
        socksPort: Int,
        host: String,
        port: Int,
        timeoutMs: Int
    ): Socket {
        val plain = Socket()
        plain.connect(InetSocketAddress(socksHost, socksPort), 6000)
        plain.soTimeout = timeoutMs
        plain.tcpNoDelay = true

        val out = DataOutputStream(plain.getOutputStream())
        val inp = DataInputStream(plain.getInputStream())

        // greeting, no auth
        out.write(byteArrayOf(0x05, 0x01, 0x00))
        out.flush()
        if (inp.readUnsignedByte() != 0x05) {
            plain.close()
            error("bad socks version")
        }
        if (inp.readUnsignedByte() != 0x00) {
            plain.close()
            error("socks auth required")
        }

        // CONNECT with remote DNS
        val hostBytes = host.toByteArray(Charsets.US_ASCII)
        val req = ByteArray(5 + hostBytes.size + 2)
        req[0] = 0x05
        req[1] = 0x01
        req[2] = 0x00
        req[3] = 0x03
        req[4] = hostBytes.size.toByte()
        System.arraycopy(hostBytes, 0, req, 5, hostBytes.size)
        req[req.size - 2] = (port ushr 8).toByte()
        req[req.size - 1] = (port and 0xFF).toByte()
        out.write(req)
        out.flush()

        if (inp.readUnsignedByte() != 0x05) {
            plain.close()
            error("bad socks reply version")
        }
        val reply = inp.readUnsignedByte()
        inp.readUnsignedByte() // RSV
        when (inp.readUnsignedByte()) {
            0x01 -> repeat(4) { inp.readUnsignedByte() }
            0x04 -> repeat(16) { inp.readUnsignedByte() }
            0x03 -> inp.readNBytes(inp.readUnsignedByte())
            else -> Unit
        }
        repeat(2) { inp.readUnsignedByte() } // PORT
        if (reply != 0x00) {
            plain.close()
            error("socks connect failed with code $reply")
        }
        return plain
    }

    private fun httpGet(
        socksHost: String,
        socksPort: Int,
        host: String,
        port: Int,
        path: String,
        timeoutMs: Int
    ): String {
        val plain = socksConnect(socksHost, socksPort, host, port, timeoutMs)
        plain.use {
            val out = DataOutputStream(it.getOutputStream())
            out.write(
                ("GET $path HTTP/1.1\r\nHost: $host\r\nUser-Agent: DeltaTor/2.0\r\n" +
                    "Accept: */*\r\nConnection: close\r\n\r\n").toByteArray(Charsets.US_ASCII)
            )
            out.flush()
            return BufferedReader(InputStreamReader(it.getInputStream(), Charsets.UTF_8))
                .use { reader -> reader.readText() }
        }
    }

    private fun httpsGet(
        socksHost: String,
        socksPort: Int,
        host: String,
        port: Int,
        path: String,
        timeoutMs: Int
    ): String {
        val plain = socksConnect(socksHost, socksPort, host, port, timeoutMs)
        val ssl = SSLSocketFactory.getDefault().createSocket(plain, host, port, true) as javax.net.ssl.SSLSocket
        ssl.use {
            // Verify the certificate chain *and* the hostname, even though the
            // connection terminates on an unknown Tor exit.
            val params = ssl.sslParameters
            params.endpointIdentificationAlgorithm = "HTTPS"
            ssl.sslParameters = params
            ssl.soTimeout = timeoutMs
            ssl.startHandshake()

            val out = DataOutputStream(ssl.getOutputStream())
            out.write(
                ("GET $path HTTP/1.1\r\nHost: $host\r\nUser-Agent: DeltaTor/2.0\r\n" +
                    "Accept: application/json\r\nConnection: close\r\n\r\n").toByteArray(Charsets.US_ASCII)
            )
            out.flush()
            return BufferedReader(InputStreamReader(ssl.getInputStream(), Charsets.UTF_8))
                .use { reader -> reader.readText() }
        }
    }
}
