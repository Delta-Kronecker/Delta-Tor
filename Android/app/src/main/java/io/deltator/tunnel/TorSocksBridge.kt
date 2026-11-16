package io.deltator.tunnel

import io.deltator.util.AppLog as Log
import java.io.BufferedOutputStream
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.io.OutputStream
import java.io.SequenceInputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * SOCKS5 bridge for the Tor tunnel types.
 *
 * Sits between hev-socks5-tunnel and Tor's SOCKS5 proxy:
 * - CONNECT (0x01): chains to Tor SOCKS5 (no auth)
 * - CONNECT (0x01) to port 53: answered locally as DNS-over-TCP, shimmed to
 *   Tor's own resolver ([TorDns]) so no name leaves through another resolver
 * - FWD_UDP (0x05) DNS (port 53): forwarded to Tor's own resolver ([TorDns]),
 *   never to a public resolver reached over Tor
 * - FWD_UDP (0x05) non-DNS: dropped silently (browser falls back to TCP CONNECT)
 *
 * DNS optimization:
 * - Concurrent: 8-thread pool for parallel DNS resolution
 * - Cached: answers are reused for 5 minutes, so a busy page rarely re-asks
 * - Fail-closed: when Tor's resolver does not answer, the query gets no answer
 *   rather than one from a resolver outside Tor
 *
 * Traffic flow:
 * App -> TUN -> hev-socks5-tunnel -> TorSocksBridge (proxyPort)
 *   TCP: -> SOCKS5 CONNECT (no auth) -> Tor SOCKS (proxyPort+1) -> Tor circuit -> Server
 *   DNS: -> FWD_UDP -> UDP to Tor's DNSPort -> Tor circuit -> resolved by the exit
 */
object TorSocksBridge {
    private const val TAG = "TorSocksBridge"
    @Volatile var debugLogging = false
    @Volatile var domainRouter: DomainRouter = DomainRouter.DISABLED
    private fun logd(msg: String) { if (debugLogging) Log.d(TAG, msg) }
    private const val BIND_MAX_RETRIES = 10
    private const val BIND_RETRY_DELAY_MS = 200L
    private const val BUFFER_SIZE = 32768
    private const val TCP_CONNECT_TIMEOUT_MS = 30000
    private const val DNS_CACHE_TTL_MS = 300_000L
    private const val DNS_TCP_PORT = 53

    private var torHost: String = "127.0.0.1"
    private var torSocksPort: Int = 0
    /** The winning runner's `DNSPort`; 0 until [start] wires it. */
    private var torDnsPort: Int = 0
    private var localAuthUsername: String? = null
    private var localAuthPassword: String? = null
    private var serverSocket: ServerSocket? = null
    private var acceptorThread: Thread? = null
    private val running = AtomicBoolean(false)
    private val connectionThreads = CopyOnWriteArrayList<Thread>()
    private var dnsExecutor = Executors.newFixedThreadPool(8)

    // DNS cache: query key (hex of query minus transaction ID) -> (response, expiry)
    private data class DnsCacheEntry(val response: ByteArray, val expiresAt: Long)
    private val dnsCache = ConcurrentHashMap<String, DnsCacheEntry>()

    fun start(
        torSocksPort: Int,
        torDnsPort: Int = 0,
        torHost: String = "127.0.0.1",
        listenPort: Int,
        listenHost: String = "127.0.0.1",
        localAuthUsername: String? = null,
        localAuthPassword: String? = null
    ): Result<Unit> {
        Log.i(TAG, "========================================")
        Log.i(TAG, "Starting Tor SOCKS5 bridge")
        Log.i(TAG, "  Tor SOCKS5: $torHost:$torSocksPort")
        Log.i(TAG, "  Tor DNS (DNSPort): $torHost:$torDnsPort")
        Log.i(TAG, "  Listen: $listenHost:$listenPort")
        Log.i(TAG, "  Local auth: ${if (!localAuthUsername.isNullOrEmpty()) "enabled" else "disabled"}")
        Log.i(TAG, "========================================")

        stop()
        this.torHost = torHost
        this.torSocksPort = torSocksPort
        this.torDnsPort = torDnsPort
        this.localAuthUsername = localAuthUsername
        this.localAuthPassword = localAuthPassword

        return try {
            val ss = bindServerSocket(listenHost, listenPort)
            serverSocket = ss
            running.set(true)

            acceptorThread = Thread({
                logd("Acceptor thread started")
                while (running.get() && !Thread.currentThread().isInterrupted) {
                    try {
                        val clientSocket = ss.accept()
                        handleConnection(clientSocket)
                    } catch (e: Exception) {
                        if (running.get()) {
                            Log.w(TAG, "Accept error: ${e.message}")
                        }
                    }
                }
                logd("Acceptor thread exited")
            }, "tor-bridge-acceptor").also { it.isDaemon = true; it.start() }

            Log.i(TAG, "Bridge started on $listenHost:$listenPort")
            Result.success(Unit)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to start bridge", e)
            stop()
            Result.failure(e)
        }
    }

    fun stop() {
        if (!running.getAndSet(false) && serverSocket == null) {
            return
        }
        logd("Stopping bridge...")

        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null

        acceptorThread?.interrupt()
        acceptorThread = null

        for (thread in connectionThreads) {
            thread.interrupt()
        }
        connectionThreads.clear()

        dnsExecutor.shutdownNow()
        dnsExecutor = Executors.newFixedThreadPool(8)

        dnsCache.clear()

        logd("Bridge stopped")
    }

    fun isRunning(): Boolean = running.get()

    /**
     * Send new connections to a different Tor SOCKS port without rebinding.
     *
     * The listener keeps its own port, so tun2socks and every app socket that was
     * already chained stay untouched: only connections opened from here on follow
     * the new runner. This is what makes a recovery invisible, the tunnel never
     * goes down while a replacement Tor boots on the same bridge.
     */
    fun repoint(torSocksPort: Int, torDnsPort: Int = 0) {
        if (this.torSocksPort != torSocksPort) {
            Log.i(TAG, "Repointing bridge: $torHost:${this.torSocksPort} -> $torHost:$torSocksPort")
            this.torSocksPort = torSocksPort
        }
        if (torDnsPort > 0 && this.torDnsPort != torDnsPort) {
            Log.i(TAG, "Repointing bridge DNS: $torHost:${this.torDnsPort} -> $torHost:$torDnsPort")
            this.torDnsPort = torDnsPort
        }
        // Cached answers were resolved over the circuit that just went away, so
        // drop them: a recovery reuses the same runner port, and returning early
        // on an unchanged port used to keep up to five minutes of name mappings
        // that were resolved by the dead Tor.
        dnsCache.clear()
    }

    fun isClientHealthy(): Boolean {
        val ss = serverSocket ?: return false
        return running.get() && !ss.isClosed
    }

    private fun bindServerSocket(host: String, port: Int): ServerSocket {
        val ss = ServerSocket()
        ss.reuseAddress = true
        var lastException: java.net.BindException? = null
        repeat(BIND_MAX_RETRIES) { attempt ->
            try {
                ss.bind(InetSocketAddress(host, port))
                if (attempt > 0) Log.i(TAG, "Port $port bound after ${attempt + 1} attempts")
                return ss
            } catch (e: java.net.BindException) {
                lastException = e
                if (attempt < BIND_MAX_RETRIES - 1) {
                    Log.w(TAG, "Port $port in use, retrying in ${BIND_RETRY_DELAY_MS}ms (attempt ${attempt + 1}/$BIND_MAX_RETRIES)")
                    Thread.sleep(BIND_RETRY_DELAY_MS)
                }
            }
        }
        ss.close()
        throw lastException ?: java.net.BindException("Failed to bind to port $port")
    }

    private fun handleConnection(clientSocket: Socket) {
        val thread = Thread({
            try {
                clientSocket.use { socket ->
                    socket.soTimeout = 30000
                    socket.tcpNoDelay = true
                    val input = socket.getInputStream()
                    val output = socket.getOutputStream()

                    // SOCKS5 greeting
                    val version = input.read()
                    if (version == -1) {
                        // EOF — readiness probe or client closed immediately; not an error
                        return@Thread
                    }
                    if (version != 0x05) {
                        Log.w(TAG, "Invalid SOCKS5 version: $version")
                        return@Thread
                    }
                    val nMethods = input.read()
                    val methods = ByteArray(nMethods)
                    input.readFully(methods)

                    // Authenticate local client
                    if (!LocalProxyAuth.handleGreeting(methods, input, output, localAuthUsername, localAuthPassword)) {
                        return@Thread
                    }

                    // SOCKS5 request
                    val ver = input.read()
                    val cmd = input.read()
                    input.read() // reserved

                    if (ver != 0x05 || (cmd != 0x01 && cmd != 0x05)) {
                        output.write(byteArrayOf(0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                        output.flush()
                        return@Thread
                    }

                    // Parse address
                    val addrType = input.read()
                    val destHost: String
                    val rawAddr: ByteArray

                    when (addrType) {
                        0x01 -> { // IPv4
                            val addr = ByteArray(4)
                            input.readFully(addr)
                            destHost = addr.joinToString(".") { (it.toInt() and 0xFF).toString() }
                            rawAddr = byteArrayOf(0x01) + addr
                        }
                        0x03 -> { // Domain name
                            val len = input.read()
                            val domain = ByteArray(len)
                            input.readFully(domain)
                            destHost = String(domain)
                            rawAddr = byteArrayOf(0x03, len.toByte()) + domain
                        }
                        0x04 -> { // IPv6
                            val addr = ByteArray(16)
                            input.readFully(addr)
                            destHost = formatIpv6(addr)
                            rawAddr = byteArrayOf(0x04) + addr
                        }
                        else -> {
                            output.write(byteArrayOf(0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                            output.flush()
                            return@Thread
                        }
                    }

                    val portHigh = input.read()
                    val portLow = input.read()
                    val destPort = (portHigh shl 8) or portLow
                    val portBytes = byteArrayOf(portHigh.toByte(), portLow.toByte())

                    // Handle FWD_UDP (cmd 0x05)
                    if (cmd == 0x05) {
                        socket.soTimeout = 0
                        handleFwdUdp(input, output)
                        return@Thread
                    }

                    // DNS-over-TCP to any resolver is answered by Tor's own
                    // resolver, not chained through to the resolver itself.
                    // Tor's DNSPort is UDP-only, so the length-prefixed query is
                    // shimmed to it and the answer re-framed for TCP; that keeps
                    // a truncation retry inside Tor instead of leaking the name
                    // to a public resolver.
                    if (destPort == DNS_TCP_PORT) {
                        socket.soTimeout = 0
                        handleDnsOverTcp(input, output)
                        return@Thread
                    }

                    // Handle CONNECT (cmd 0x01) — chain through Tor SOCKS5
                    handleConnect(destHost, destPort, rawAddr, portBytes, socket, input, output)
                }
            } catch (e: Exception) {
                if (running.get()) {
                    logd("Connection handler error: ${e.message}")
                }
            }
        }, "tor-bridge-handler")
        thread.isDaemon = true
        connectionThreads.add(thread)
        thread.start()

        connectionThreads.removeAll { !it.isAlive }
    }

    /**
     * Handle SOCKS5 CONNECT by chaining through Tor's SOCKS5 proxy (no auth).
     * With domain routing: sniffs TLS SNI / HTTP Host to decide bypass vs tunnel.
     */
    private fun handleConnect(
        destHost: String,
        destPort: Int,
        rawAddr: ByteArray,
        portBytes: ByteArray,
        clientSocket: Socket,
        clientInput: InputStream,
        clientOutput: OutputStream
    ) {
        val router = domainRouter
        if (router.enabled) {
            handleConnectWithRouting(router, destHost, destPort, rawAddr, portBytes, clientSocket, clientInput, clientOutput)
            return
        }

        // Original flow — no domain routing
        connectViaTor(destHost, destPort, rawAddr, portBytes, clientSocket, clientInput, clientOutput, sendReply = true)
    }

    private fun handleConnectWithRouting(
        router: DomainRouter,
        destHost: String,
        destPort: Int,
        rawAddr: ByteArray,
        portBytes: ByteArray,
        clientSocket: Socket,
        clientInput: InputStream,
        clientOutput: OutputStream
    ) {
        var effectiveHost = destHost
        var sniffBuffer: ByteArray? = null
        var sniffLen = 0
        var wasEarlyReply = false

        if (DomainRouter.isIpAddress(destHost)) {
            // IP address — sniff to recover domain
            clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
            clientOutput.flush()
            clientSocket.soTimeout = 3000
            wasEarlyReply = true

            val result = ProtocolSniffer.sniff(clientInput)
            if (result.domain != null) {
                effectiveHost = result.domain
                logd("CONNECT: sniffed domain=$effectiveHost from IP=$destHost")
            }
            sniffBuffer = result.bufferedData
            sniffLen = result.bufferedLength
        }

        if (router.shouldBypass(effectiveHost)) {
            // Direct connection — bypass tunnel
            logd("CONNECT: bypassing tunnel for $effectiveHost:$destPort")
            try {
                val directSocket = router.createDirectConnection(destHost, destPort)
                if (!wasEarlyReply) {
                    clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                    clientOutput.flush()
                }
                clientSocket.soTimeout = 0
                val effectiveInput = if (sniffLen > 0)
                    SequenceInputStream(ByteArrayInputStream(sniffBuffer!!, 0, sniffLen), clientInput)
                else clientInput
                bridgeDirect(effectiveInput, clientOutput, directSocket)
            } catch (e: Exception) {
                logd("CONNECT: direct connection failed for $effectiveHost:$destPort: ${e.message}")
                if (!wasEarlyReply) {
                    try {
                        clientOutput.write(byteArrayOf(0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                        clientOutput.flush()
                    } catch (_: Exception) {}
                }
            }
            return
        }

        // Tunnel path
        clientSocket.soTimeout = 0
        val effectiveInput = if (sniffLen > 0)
            SequenceInputStream(ByteArrayInputStream(sniffBuffer!!, 0, sniffLen), clientInput)
        else clientInput
        connectViaTor(destHost, destPort, rawAddr, portBytes, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
    }

    /**
     * Connect through Tor SOCKS5 and bridge bidirectionally.
     * If [sendReply] is true, sends SOCKS5 success/failure replies to client.
     * If false, success reply was already sent (early reply for sniffing).
     */
    private fun connectViaTor(
        destHost: String,
        destPort: Int,
        rawAddr: ByteArray,
        portBytes: ByteArray,
        clientSocket: Socket,
        clientInput: InputStream,
        clientOutput: OutputStream,
        sendReply: Boolean
    ) {
        val remoteSocket: Socket
        try {
            remoteSocket = Socket()
            remoteSocket.connect(InetSocketAddress(torHost, torSocksPort), TCP_CONNECT_TIMEOUT_MS)
            remoteSocket.tcpNoDelay = true
        } catch (e: Exception) {
            logd("CONNECT: failed to connect to Tor SOCKS5: ${e.message}")
            if (sendReply) {
                clientOutput.write(byteArrayOf(0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                clientOutput.flush()
            }
            return
        }

        try {
            val remoteInput = remoteSocket.getInputStream()
            val remoteOutput = remoteSocket.getOutputStream()

            // SOCKS5 greeting to Tor (no auth)
            remoteOutput.write(byteArrayOf(0x05, 0x01, 0x00))
            remoteOutput.flush()

            val greetResp = ByteArray(2)
            remoteInput.readFully(greetResp)
            if (greetResp[0] != 0x05.toByte() || (greetResp[1].toInt() and 0xFF) == 0xFF) {
                Log.w(TAG, "CONNECT: Tor rejected greeting")
                if (sendReply) {
                    clientOutput.write(byteArrayOf(0x05, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                    clientOutput.flush()
                }
                remoteSocket.close()
                return
            }

            // SOCKS5 CONNECT request to Tor
            val connectReq = byteArrayOf(0x05, 0x01, 0x00) + rawAddr + portBytes
            remoteOutput.write(connectReq)
            remoteOutput.flush()

            // Read CONNECT response header
            val connRespHeader = ByteArray(4)
            remoteInput.readFully(connRespHeader)

            if (connRespHeader[1] != 0x00.toByte()) {
                logd("CONNECT: Tor rejected to $destHost:$destPort (rep=${connRespHeader[1]})")
                if (sendReply) {
                    clientOutput.write(byteArrayOf(0x05, connRespHeader[1], 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                    clientOutput.flush()
                }
                remoteSocket.close()
                return
            }

            // Read remaining response bytes based on address type
            when (connRespHeader[3].toInt() and 0xFF) {
                0x01 -> { val rest = ByteArray(6); remoteInput.readFully(rest) }
                0x03 -> { val len = remoteInput.read(); val rest = ByteArray(len + 2); remoteInput.readFully(rest) }
                0x04 -> { val rest = ByteArray(18); remoteInput.readFully(rest) }
            }

            logd("CONNECT: $destHost:$destPort OK (via Tor)")

            // Send success to hev-socks5-tunnel (if not already sent)
            if (sendReply) {
                clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                clientOutput.flush()
            }

            clientSocket.soTimeout = 0

            // Bridge bidirectionally
            remoteSocket.use { remote ->
                val t1 = Thread({
                    try {
                        copyStream(clientInput, remoteOutput)
                    } catch (_: Exception) {
                    } finally {
                        try { remoteOutput.close() } catch (_: Exception) {}
                    }
                }, "tor-bridge-c2s")
                t1.isDaemon = true
                t1.start()

                try {
                    copyStream(remoteInput, clientOutput)
                } catch (_: Exception) {
                } finally {
                    try { remote.close() } catch (_: Exception) {}
                    t1.interrupt()
                }
            }
        } catch (e: Exception) {
            logd("CONNECT: chain error for $destHost:$destPort: ${e.message}")
            if (sendReply) {
                try {
                    clientOutput.write(byteArrayOf(0x05, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                    clientOutput.flush()
                } catch (_: Exception) {}
            }
            try { remoteSocket.close() } catch (_: Exception) {}
        }
    }

    /**
     * Handle FWD_UDP (cmd 0x05) — same wire format as other bridges.
     * DNS (port 53): forwarded to Tor's own resolver, so no query ever leaves
     * the device in the clear and none is answered by a public resolver either.
     * Non-DNS UDP: dropped silently.
     *
     * DNS queries are dispatched to a thread pool for concurrent resolution.
     * Responses are cached for 5 minutes to keep repeat lookups off the wire.
     */
    private fun handleFwdUdp(input: InputStream, output: OutputStream) {
        output.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
        output.flush()

        logd("FWD_UDP session established")

        while (running.get() && !Thread.currentThread().isInterrupted) {
            val hdr = ByteArray(3)
            input.readFully(hdr)

            val datLen = ((hdr[0].toInt() and 0xFF) shl 8) or (hdr[1].toInt() and 0xFF)
            val hdrLen = hdr[2].toInt() and 0xFF
            val addrLen = hdrLen - 3

            if (addrLen <= 0 || datLen <= 0) {
                Log.w(TAG, "FWD_UDP: invalid header (datLen=$datLen, hdrLen=$hdrLen)")
                break
            }

            val addrBytes = ByteArray(addrLen)
            input.readFully(addrBytes)

            val payload = ByteArray(datLen)
            input.readFully(payload)

            val dest = parseSocksAddress(addrBytes)
            if (dest == null) {
                Log.w(TAG, "FWD_UDP: failed to parse address")
                continue
            }

            if (dest.second != 53) {
                // Non-DNS UDP: drop silently
                continue
            }

            // Dispatch DNS query to thread pool for concurrent resolution
            val addrCopy = addrBytes.copyOf()
            val payloadCopy = payload.copyOf()
            dnsExecutor.submit {
                try {
                    // Check cache first
                    val cached = getCachedDns(payloadCopy)
                    val response = if (cached != null) {
                        logd("DNS: cache hit")
                        cached
                    } else {
                        val remote = resolveViaTorDns(payloadCopy)
                        if (remote != null) cacheDnsResponse(payloadCopy, remote)
                        remote
                    }

                    if (response != null && response.isNotEmpty()) {
                        val respHdr = ByteArray(3)
                        respHdr[0] = ((response.size shr 8) and 0xFF).toByte()
                        respHdr[1] = (response.size and 0xFF).toByte()
                        respHdr[2] = (3 + addrCopy.size).toByte()

                        synchronized(output) {
                            output.write(respHdr)
                            output.write(addrCopy)
                            output.write(response)
                            output.flush()
                        }
                    }
                } catch (e: Exception) {
                    logd("FWD_UDP: DNS forward failed: ${e.message}")
                }
            }
        }

        logd("FWD_UDP session ended")
    }

    // --- DNS cache ---

    /**
     * Cache key: hex of DNS query bytes after the 2-byte transaction ID.
     * This way the same query with different TXIDs hits the cache.
     */
    private fun dnsCacheKey(query: ByteArray): String {
        if (query.size <= 2) return ""
        val sb = StringBuilder((query.size - 2) * 2)
        for (i in 2 until query.size) {
            sb.append("%02x".format(query[i]))
        }
        return sb.toString()
    }

    private fun getCachedDns(query: ByteArray): ByteArray? {
        val key = dnsCacheKey(query)
        if (key.isEmpty()) return null
        val entry = dnsCache[key] ?: return null
        if (System.currentTimeMillis() > entry.expiresAt) {
            dnsCache.remove(key)
            return null
        }
        // Copy cached response and replace transaction ID with query's
        val response = entry.response.copyOf()
        if (response.size >= 2 && query.size >= 2) {
            response[0] = query[0]
            response[1] = query[1]
        }
        return response
    }

    private fun cacheDnsResponse(query: ByteArray, response: ByteArray) {
        val key = dnsCacheKey(query)
        if (key.isEmpty()) return
        dnsCache[key] = DnsCacheEntry(response.copyOf(), System.currentTimeMillis() + DNS_CACHE_TTL_MS)
        // Evict old entries periodically (simple: if cache gets large)
        if (dnsCache.size > 500) {
            val now = System.currentTimeMillis()
            dnsCache.entries.removeIf { now > it.value.expiresAt }
        }
    }

    // --- DNS via Tor's own resolver ---

    /**
     * Ask Tor's own resolver (the winning runner's `DNSPort`) about [payload].
     *
     * A null means Tor did not answer in time. The caller must *not* fall back to
     * a resolver outside Tor, so a lookup that Tor cannot answer fails closed.
     */
    private fun resolveViaTorDns(payload: ByteArray): ByteArray? {
        val port = torDnsPort
        if (port <= 0) {
            Log.w(TAG, "DNS: no Tor DNSPort wired yet; failing the query closed")
            return null
        }
        return TorDns.query(port, payload)
    }

    /**
     * Serve DNS-over-TCP (a resolver's truncation retry) from Tor's own resolver.
     *
     * Tor's `DNSPort` answers UDP only, so each length-prefixed query is shimmed
     * to it and the answer framed back for TCP. Nothing is ever sent to the
     * resolver named in the CONNECT, so a truncated UDP answer cannot pull the
     * name out of Tor.
     */
    private fun handleDnsOverTcp(input: InputStream, output: OutputStream) {
        // Acknowledge the SOCKS5 CONNECT before serving, exactly like the
        // tunnelled path: hev-socks5-tunnel waits for this reply.
        output.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
        output.flush()
        logd("CONNECT: DNS-over-TCP served by Tor's own resolver")

        while (running.get() && !Thread.currentThread().isInterrupted) {
            val lenBuf = ByteArray(2)
            try {
                input.readFully(lenBuf)
            } catch (_: Exception) {
                break
            }
            val len = ((lenBuf[0].toInt() and 0xFF) shl 8) or (lenBuf[1].toInt() and 0xFF)
            if (len <= 0) break

            val query = ByteArray(len)
            try {
                input.readFully(query)
            } catch (_: Exception) {
                break
            }

            val cached = getCachedDns(query)
            val response = if (cached != null) {
                logd("DNS/TCP: cache hit")
                cached
            } else {
                val answer = resolveViaTorDns(query)
                if (answer != null) cacheDnsResponse(query, answer)
                answer
            }
            if (response == null || response.isEmpty()) {
                // Fail closed: closing makes the client retry, rather than hand
                // it an answer resolved outside Tor.
                break
            }

            output.write(
                byteArrayOf(
                    ((response.size shr 8) and 0xFF).toByte(),
                    (response.size and 0xFF).toByte()
                )
            )
            output.write(response)
            output.flush()
        }
    }

    private fun parseSocksAddress(addrBytes: ByteArray): Pair<String, Int>? {
        if (addrBytes.isEmpty()) return null

        return when (addrBytes[0].toInt() and 0xFF) {
            0x01 -> {
                if (addrBytes.size < 7) return null
                val host = "${addrBytes[1].toInt() and 0xFF}.${addrBytes[2].toInt() and 0xFF}.${addrBytes[3].toInt() and 0xFF}.${addrBytes[4].toInt() and 0xFF}"
                val port = ((addrBytes[5].toInt() and 0xFF) shl 8) or (addrBytes[6].toInt() and 0xFF)
                Pair(host, port)
            }
            0x03 -> {
                val len = addrBytes[1].toInt() and 0xFF
                if (addrBytes.size < 2 + len + 2) return null
                val host = String(addrBytes, 2, len)
                val port = ((addrBytes[2 + len].toInt() and 0xFF) shl 8) or (addrBytes[3 + len].toInt() and 0xFF)
                Pair(host, port)
            }
            0x04 -> {
                if (addrBytes.size < 19) return null
                val parts = mutableListOf<String>()
                for (i in 0 until 16 step 2) {
                    val value = ((addrBytes[1 + i].toInt() and 0xFF) shl 8) or (addrBytes[2 + i].toInt() and 0xFF)
                    parts.add(String.format("%x", value))
                }
                val host = parts.joinToString(":")
                val port = ((addrBytes[17].toInt() and 0xFF) shl 8) or (addrBytes[18].toInt() and 0xFF)
                Pair(host, port)
            }
            else -> null
        }
    }

    private fun bridgeDirect(clientInput: InputStream, clientOutput: OutputStream, directSocket: Socket) {
        directSocket.use { remote ->
            remote.tcpNoDelay = true
            val remoteInput = remote.getInputStream()
            val remoteOutput = remote.getOutputStream()

            val t1 = Thread({
                try {
                    copyStream(clientInput, remoteOutput)
                } catch (_: Exception) {
                } finally {
                    try { remoteOutput.close() } catch (_: Exception) {}
                }
            }, "tor-bridge-direct-c2s")
            t1.isDaemon = true
            t1.start()

            try {
                copyStream(remoteInput, clientOutput)
            } catch (_: Exception) {
            } finally {
                try { remote.close() } catch (_: Exception) {}
                t1.interrupt()
            }
        }
    }

    private fun copyStream(input: InputStream, output: OutputStream) {
        val buffered = BufferedOutputStream(output, BUFFER_SIZE)
        val buffer = ByteArray(BUFFER_SIZE)
        while (!Thread.currentThread().isInterrupted) {
            val bytesRead = input.read(buffer)
            if (bytesRead == -1) break
            buffered.write(buffer, 0, bytesRead)
            if (bytesRead < BUFFER_SIZE || input.available() == 0) {
                buffered.flush()
            }
        }
        buffered.flush()
    }

    private fun InputStream.readFully(buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val bytesRead = this.read(buffer, offset, buffer.size - offset)
            if (bytesRead == -1) throw java.io.IOException("Unexpected end of stream")
            offset += bytesRead
        }
    }

    private fun formatIpv6(addr: ByteArray): String {
        val parts = mutableListOf<String>()
        for (i in 0 until 16 step 2) {
            val value = ((addr[i].toInt() and 0xFF) shl 8) or (addr[i + 1].toInt() and 0xFF)
            parts.add(String.format("%x", value))
        }
        return parts.joinToString(":")
    }
}
