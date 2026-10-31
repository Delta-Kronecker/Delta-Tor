package io.deltator.feature.warp

import android.util.Base64
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.math.BigInteger
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * WarpProfile: holds Cloudflare WARP client configuration and WireGuard credentials.
 */
data class WarpProfile(
    val privateKey: String,
    val publicKey: String,
    val addressV4: String,
    val addressV6: String,
    val peerPublicKey: String,
    val endpoint: String,
    val reserved: ByteArray,
    val accountId: String,
    val token: String
) {
    fun toJson(): String {
        val obj = JSONObject()
        obj.put("privateKey", privateKey)
        obj.put("publicKey", publicKey)
        obj.put("addressV4", addressV4)
        obj.put("addressV6", addressV6)
        obj.put("peerPublicKey", peerPublicKey)
        obj.put("endpoint", endpoint)
        obj.put("reserved", Base64.encodeToString(reserved, Base64.NO_WRAP))
        obj.put("accountId", accountId)
        obj.put("token", token)
        return obj.toString()
    }

    companion object {
        fun fromJson(jsonStr: String): WarpProfile? = try {
            val obj = JSONObject(jsonStr)
            val reservedBytes = Base64.decode(obj.optString("reserved", ""), Base64.DEFAULT)
            WarpProfile(
                privateKey = obj.getString("privateKey"),
                publicKey = obj.getString("publicKey"),
                addressV4 = obj.optString("addressV4", "172.16.0.2/32"),
                addressV6 = obj.optString("addressV6", ""),
                peerPublicKey = obj.optString("peerPublicKey", "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo="),
                endpoint = obj.optString("endpoint", "162.159.192.1:2408"),
                reserved = reservedBytes,
                accountId = obj.optString("accountId", ""),
                token = obj.optString("token", "")
            )
        } catch (_: Exception) {
            null
        }
    }
}

/**
 * 1-Click Cloudflare WARP Auto-Registrar & Anycast Endpoint Manager.
 *
 * Proposal #3:
 * Generates Curve25519 / X25519 keypair, registers via Cloudflare client API,
 * extracts client_id (reserved bytes), and provides Anycast endpoint rotation
 * when certain IP ranges are throttled or blocked by DPI.
 */
object WarpRegistrar {
    private const val TAG = "WarpRegistrar"
    private const val REGISTRATION_URL = "https://api.cloudflareclient.com/v0a2158/reg"

    // Official Cloudflare Anycast IP pool for DPI bypass rotation
    val ANYCAST_ENDPOINTS = listOf(
        "162.159.192.1:2408",
        "162.159.193.1:2408",
        "162.159.195.1:2408",
        "188.114.96.1:2408",
        "188.114.97.1:2408",
        "188.114.98.1:2408",
        "188.114.99.1:2408",
        "162.159.204.1:2408",
        "162.159.192.1:500",
        "162.159.193.1:1701",
        "162.159.195.1:4500"
    )

    /**
     * Self-contained Montgomery Curve25519 scalar multiplication:
     * Derives public key (32 bytes) from private key (32 bytes).
     */
    fun derivePublicKey(privateKey: ByteArray): ByteArray {
        val p = BigInteger.valueOf(2).pow(255).subtract(BigInteger.valueOf(19))
        val a24 = BigInteger.valueOf(121665)

        val clamped = privateKey.copyOf(32)
        clamped[0] = (clamped[0].toInt() and 248).toByte()
        clamped[31] = (clamped[31].toInt() and 127).toByte()
        clamped[31] = (clamped[31].toInt() or 64).toByte()

        var k = BigInteger.ZERO
        for (i in 0 until 32) {
            k = k.add(BigInteger.valueOf(clamped[i].toLong() and 0xFF).shiftLeft(i * 8))
        }

        var x1 = BigInteger.valueOf(9)
        var x2 = BigInteger.ONE
        var z2 = BigInteger.ZERO
        var x3 = x1
        var z3 = BigInteger.ONE

        var swap = 0
        for (t in 254 downTo 0) {
            val kt = (k.shiftRight(t).and(BigInteger.ONE)).toInt()
            swap = swap xor kt
            if (swap != 0) {
                var tmp = x2; x2 = x3; x3 = tmp
                tmp = z2; z2 = z3; z3 = tmp
            }
            swap = kt

            val a = x2.add(z2).mod(p)
            val aa = a.multiply(a).mod(p)
            val b = x2.subtract(z2).mod(p)
            val bb = b.multiply(b).mod(p)
            val e = aa.subtract(bb).mod(p)
            val c = x3.add(z3).mod(p)
            val d = x3.subtract(z3).mod(p)
            val da = d.multiply(a).mod(p)
            val cb = c.multiply(b).mod(p)

            x3 = da.add(cb).mod(p).pow(2).mod(p)
            z3 = x1.multiply(da.subtract(cb).mod(p).pow(2)).mod(p)
            x2 = aa.multiply(bb).mod(p)
            z2 = e.multiply(aa.add(a24.multiply(e))).mod(p)
        }

        if (swap != 0) {
            var tmp = x2; x2 = x3; x3 = tmp
            tmp = z2; z2 = z3; z3 = tmp
        }

        val result = x2.multiply(z2.modInverse(p)).mod(p)
        val pubBytes = ByteArray(32)
        var rem = result
        for (i in 0 until 32) {
            pubBytes[i] = rem.and(BigInteger.valueOf(0xFF)).toByte()
            rem = rem.shiftRight(8)
        }
        return pubBytes
    }

    /**
     * Generate a new Curve25519 keypair.
     * Returns Pair(privateKeyBase64, publicKeyBase64).
     */
    fun generateKeyPair(): Pair<String, String> {
        val random = SecureRandom()
        val priv = ByteArray(32)
        random.nextBytes(priv)
        val pub = derivePublicKey(priv)
        return Base64.encodeToString(priv, Base64.NO_WRAP) to Base64.encodeToString(pub, Base64.NO_WRAP)
    }

    /**
     * Registers a new WARP profile via official API.
     */
    suspend fun registerWarp(): Result<WarpProfile> = withContext(Dispatchers.IO) {
        try {
            val (privKey, pubKey) = generateKeyPair()
            val df = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US)
            df.timeZone = TimeZone.getTimeZone("UTC")
            val nowIso = df.format(Date())

            val payload = JSONObject().apply {
                put("key", pubKey)
                put("install_id", "")
                put("fcm_token", "")
                put("tos", nowIso)
                put("model", "Android")
                put("type", "Android")
                put("locale", "en_US")
            }

            Log.i(TAG, "Registering Cloudflare WARP client with public key $pubKey")
            val url = URL(REGISTRATION_URL)
            val conn = url.openConnection() as HttpURLConnection
            conn.requestMethod = "POST"
            conn.connectTimeout = 10000
            conn.readTimeout = 10000
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json; charset=UTF-8")
            conn.setRequestProperty("User-Agent", "okhttp/3.12.1")

            conn.outputStream.use { os ->
                OutputStreamWriter(os, Charsets.UTF_8).use { it.write(payload.toString()) }
            }

            val code = conn.responseCode
            if (code !in 200..204) {
                val err = conn.errorStream?.bufferedReader()?.readText() ?: "HTTP $code"
                Log.e(TAG, "WARP registration failed: $code $err")
                return@withContext Result.failure(RuntimeException("WARP registration error: $code $err"))
            }

            val body = conn.inputStream.bufferedReader().readText()
            val json = JSONObject(body)

            val id = json.optString("id", "")
            val token = json.optString("token", "")
            val config = json.getJSONObject("config")
            val iface = config.getJSONObject("interface")
            val addrs = iface.getJSONObject("addresses")
            val v4 = addrs.optString("v4", "172.16.0.2/32")
            val v6 = addrs.optString("v6", "")

            val peers = config.getJSONArray("peers")
            val peer0 = peers.getJSONObject(0)
            val peerPubKey = peer0.optString("public_key", "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=")
            val endpointObj = peer0.optJSONObject("endpoint")
            val endpointHost = endpointObj?.optString("host", "162.159.192.1:2408") ?: "162.159.192.1:2408"

            // Extract reserved bytes: client_id in Cloudflare API is 3 bytes (encoded in base64 or hex)
            val clientIdRaw = config.optString("client_id", "")
            val reservedBytes = if (clientIdRaw.isNotEmpty()) {
                try { Base64.decode(clientIdRaw, Base64.DEFAULT) } catch (_: Exception) { ByteArray(3) }
            } else {
                ByteArray(3)
            }

            val profile = WarpProfile(
                privateKey = privKey,
                publicKey = pubKey,
                addressV4 = v4,
                addressV6 = v6,
                peerPublicKey = peerPubKey,
                endpoint = endpointHost,
                reserved = reservedBytes,
                accountId = id,
                token = token
            )

            Log.i(TAG, "WARP profile successfully registered! Address: $v4, Reserved: ${reservedBytes.size} bytes")
            Result.success(profile)
        } catch (e: Exception) {
            Log.e(TAG, "WARP registration exception", e)
            Result.failure(e)
        }
    }
}
