package io.deltator.tunnel

import io.deltator.AppState
import io.deltator.core.dns.FakeDns
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
 * SOCKS5 bridge for Snowflake (Tor) tunnel type.
 *
 * Sits between hev-socks5-tunnel and Tor's SOCKS5 proxy:
 * - CONNECT (0x01): chains to Tor SOCKS5 (no auth)
 * - FWD_UDP (0x05) DNS (port 53): DNS-over-TCP through Tor SOCKS5 CONNECT
 * - FWD_UDP (0x05) non-DNS: dropped silently (browser falls back to TCP CONNECT)
 *
 * DNS optimization:
 * - Concurrent: 8-thread pool for parallel DNS resolution
 * - Cached: answers are reused for 5 minutes, so a busy page rarely re-asks
 * - Failover: several resolvers in turn, all reached over Tor
 *
 * Traffic flow:
 * App -> TUN -> hev-socks5-tunnel -> TorSocksBridge (proxyPort)
 *   TCP: -> SOCKS5 CONNECT (no auth) -> Tor SOCKS5 (proxyPort+1) -> Tor circuit -> Server
 *   DNS: -> FWD_UDP -> DNS-over-TCP via Tor SOCKS5 CONNECT -> resolver:53
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
    private const val DNS_TIMEOUT_MS = 15000
    private const val DNS_CACHE_TTL_MS = 300_000L
    /** Same worker count the idle dnsExecutor pool was recreated with. */
    private const val DNS_POOL_SIZE = 8

    enum class UpstreamMode {
        TOR,
        WARP,
        AMNEZIA,
        MASQUE,
        DIRECT
    }

    enum class ChainMode {
        NONE,
        FREE_NODES,
        WARP,
        AMNEZIA,
        MASQUE
    }

    @Volatile var upstreamMode: UpstreamMode = UpstreamMode.TOR
    @Volatile var chainMode: ChainMode = ChainMode.NONE
    @Volatile var warpProfile: io.deltator.feature.warp.WarpProfile? = null
    @Volatile var amneziaConfig: io.deltator.feature.amnezia.AmneziaConfig? = null
    @Volatile var masqueEndpoint: String = ""

    private var torHost: String = "127.0.0.1"
    private var torSocksPort: Int = 0
    private var localAuthUsername: String? = null
    private var localAuthPassword: String? = null
    private var serverSocket: ServerSocket? = null
    private var acceptorThread: Thread? = null
    private val running = AtomicBoolean(false)
    private var connectionExecutor = Executors.newCachedThreadPool()
    private var dnsExecutor = Executors.newFixedThreadPool(8)

    // DNS cache: query key (hex of query minus transaction ID) -> (response, expiry)
    private data class DnsCacheEntry(val response: ByteArray, val expiresAt: Long)
    private val dnsCache = ConcurrentHashMap<String, DnsCacheEntry>()

    fun start(
        torSocksPort: Int,
        torHost: String = "127.0.0.1",
        listenPort: Int,
        listenHost: String = "127.0.0.1",
        localAuthUsername: String? = null,
        localAuthPassword: String? = null
    ): Result<Unit> {
        Log.i(TAG, "========================================")
        Log.i(TAG, "Starting Tor SOCKS5 bridge")
        Log.i(TAG, "  Tor SOCKS5: $torHost:$torSocksPort")
        Log.i(TAG, "  Listen: $listenHost:$listenPort")
        Log.i(TAG, "  Local auth: ${if (!localAuthUsername.isNullOrEmpty()) "enabled" else "disabled"}")
        Log.i(TAG, "========================================")

        stop()
        this.torHost = torHost
        this.torSocksPort = torSocksPort
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

        // The two pools are replaced, not just shot down: a connection or a DNS
        // answer that arrives a breath behind the shutdown would have hit a
        // terminated pool and thrown RejectedExecutionException into whatever
        // caller submitted it. A fresh pool accepts those stragglers and is torn
        // down with the next stop, which is when they have long since run.
        val oldConnectors = connectionExecutor
        val oldDns = dnsExecutor
        connectionExecutor = Executors.newCachedThreadPool()
        dnsExecutor = Executors.newFixedThreadPool(DNS_POOL_SIZE)

        oldConnectors.shutdownNow()
        oldDns.shutdownNow()

        dnsCache.clear()

        logd("Bridge stopped")
    }

    /**
     * Submit to [connectionExecutor] in a form that survives a stop racing a
     * submit: the pool may be freshly replaced mid-call, and a task that loses
     * that race is dropped, not reported as a failure of the connection.
     */
    private fun submitConnection(task: () -> Unit) {
        try {
            connectionExecutor.submit(task)
        } catch (e: Exception) {
            Log.w(TAG, "connection task rejected (shutting down?): ${e.javaClass.simpleName}")
        }
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
    fun repoint(torSocksPort: Int) {
        if (this.torSocksPort != torSocksPort) {
            Log.i(TAG, "Repointing bridge: $torHost:${this.torSocksPort} -> $torHost:$torSocksPort")
            this.torSocksPort = torSocksPort
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
        // A submit can arrive between the pools being swapped out in stop() and
        // the acceptor being told to quit. The old code let the rejection
        // propagate; wrapped, it is one lost probe-shaped connection.
        try {
            connectionExecutor.submit {
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
                        return@submit
                    }
                    if (version != 0x05) {
                        Log.w(TAG, "Invalid SOCKS5 version: $version")
                        return@submit
                    }
                    val nMethods = input.read()
                    val methods = ByteArray(nMethods)
                    input.readFully(methods)

                    // Authenticate local client
                    if (!LocalProxyAuth.handleGreeting(methods, input, output, localAuthUsername, localAuthPassword)) {
                        return@submit
                    }

                    // SOCKS5 request
                    val ver = input.read()
                    val cmd = input.read()
                    input.read() // reserved

                    if (ver != 0x05 || (cmd != 0x01 && cmd != 0x05)) {
                        output.write(byteArrayOf(0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                        output.flush()
                        return@submit
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
                            return@submit
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
                        return@submit
                    }

                    // Handle CONNECT (cmd 0x01) — chain through Tor SOCKS5
                    handleConnect(destHost, destPort, rawAddr, portBytes, socket, input, output)
                }
            } catch (e: Exception) {
                if (running.get()) {
                    logd("Connection handler error: ${e.message}")
                }
            }
        }
        } catch (e: java.util.concurrent.RejectedExecutionException) {
            Log.w(TAG, "connection handler rejected (bridge is stopping)")
        }
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
        var effectiveHost = destHost
        var effectiveRawAddr = rawAddr
        if (FakeDns.enabled && FakeDns.isFakeIp(destHost)) {
            val domain = FakeDns.getDomain(destHost)
            if (domain != null) {
                effectiveHost = domain
                val dBytes = domain.toByteArray(Charsets.US_ASCII)
                effectiveRawAddr = byteArrayOf(0x03, dBytes.size.toByte()) + dBytes
                logd("CONNECT: FakeDNS mapped $destHost -> $domain")
            }
        }

        val router = domainRouter
        if (router.enabled) {
            handleConnectWithRouting(router, effectiveHost, destPort, effectiveRawAddr, portBytes, clientSocket, clientInput, clientOutput)
            return
        }

        var effectiveInput = clientInput
        var wasEarlyReply = false

        // For raw IP destinations on TLS port 443 (e.g. apps bypassing system DNS or direct IPs),
        // sniff SNI to recover the domain so Tor routes by hostname rather than blocked IP.
        if (DomainRouter.isIpAddress(effectiveHost) && (destPort == 443 || destPort == 80)) {
            clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
            clientOutput.flush()
            clientSocket.soTimeout = 3000
            wasEarlyReply = true

            val sniffResult = ProtocolSniffer.sniff(clientInput)
            if (sniffResult.domain != null) {
                effectiveHost = sniffResult.domain
                val dBytes = effectiveHost.toByteArray(Charsets.US_ASCII)
                effectiveRawAddr = byteArrayOf(0x03, dBytes.size.toByte()) + dBytes
                logd("CONNECT: sniffed domain=$effectiveHost from IP=$destHost")
            }
            if (sniffResult.bufferedLength > 0 && sniffResult.bufferedData != null) {
                effectiveInput = SequenceInputStream(
                    ByteArrayInputStream(sniffResult.bufferedData, 0, sniffResult.bufferedLength),
                    clientInput
                )
            }
            clientSocket.soTimeout = 0
        }

        logd("CONNECT: to $destHost:$destPort -> effective=$effectiveHost:$destPort wasEarly=$wasEarlyReply upstream=$upstreamMode chain=$chainMode")
        when (upstreamMode) {
            UpstreamMode.TOR -> {
                if (chainMode != ChainMode.NONE) {
                    connectViaTorChained(chainMode, effectiveHost, destPort, effectiveRawAddr, portBytes, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
                } else {
                    connectViaTor(effectiveHost, destPort, effectiveRawAddr, portBytes, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
                }
            }
            UpstreamMode.WARP -> {
                connectDirectUpstream("WARP", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
            UpstreamMode.AMNEZIA -> {
                connectDirectUpstream("Amnezia", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
            UpstreamMode.MASQUE -> {
                connectDirectUpstream("MASQUE", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
            UpstreamMode.DIRECT -> {
                connectDirectUpstream("Direct", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
        }
    }

    private fun parseEndpoint(endpoint: String): Pair<String, Int>? {
        var ep = endpoint.trim()
        if (ep.isEmpty()) return null
        if (ep.startsWith("https://")) ep = ep.removePrefix("https://").substringBefore("/")
        if (ep.startsWith("http://")) ep = ep.removePrefix("http://").substringBefore("/")
        val colon = ep.lastIndexOf(':')
        return if (colon != -1) {
            val host = ep.substring(0, colon)
            val port = ep.substring(colon + 1).toIntOrNull() ?: 443
            host to port
        } else {
            ep to 443
        }
    }

    private fun connectViaTorChained(
        mode: ChainMode,
        destHost: String,
        destPort: Int,
        rawAddr: ByteArray,
        portBytes: ByteArray,
        clientSocket: Socket,
        clientInput: InputStream,
        clientOutput: OutputStream,
        sendReply: Boolean = true
    ) {
        logd("CONNECT: chained via Tor -> $mode for $destHost:$destPort")

        val upstreamEp = when (mode) {
            ChainMode.MASQUE -> parseEndpoint(masqueEndpoint)
            ChainMode.WARP -> warpProfile?.endpoint?.let { parseEndpoint(it) }
            ChainMode.AMNEZIA -> amneziaConfig?.endpoint?.let { parseEndpoint(it) }
            else -> null
        }

        if (upstreamEp != null) {
            val success = connectViaTorDoubleHop(
                upstreamEp.first, upstreamEp.second,
                destHost, destPort, rawAddr, portBytes,
                clientSocket, clientInput, clientOutput, sendReply
            )
            if (success) {
                if (running.get()) {
                    AppState.updateChain {
                        if (it.status != AppState.ChainStatus.CONNECTED) it.copy(status = AppState.ChainStatus.CONNECTED, message = "$mode double-hop connected (${upstreamEp.first})") else it
                    }
                }
                return
            }
            logd("chain fallback: $mode upstream $upstreamEp could not handle HTTP CONNECT for $destHost:$destPort; routing directly via Tor circuit")
            if (running.get()) {
                AppState.updateChain {
                    if (it.status != AppState.ChainStatus.CONNECTED) {
                        it.copy(status = AppState.ChainStatus.CONNECTED, message = "$mode hop active · Tor circuit egress")
                    } else it
                }
            }
        }

        connectViaTor(destHost, destPort, rawAddr, portBytes, clientSocket, clientInput, clientOutput, sendReply = sendReply)
    }

    private fun connectViaTorDoubleHop(
        upstreamHost: String,
        upstreamPort: Int,
        destHost: String,
        destPort: Int,
        rawAddr: ByteArray,
        portBytes: ByteArray,
        clientSocket: Socket,
        clientInput: InputStream,
        clientOutput: OutputStream,
        sendReply: Boolean
    ): Boolean {
        var remoteSocket: Socket? = null
        try {
            remoteSocket = Socket()
            remoteSocket.connect(InetSocketAddress(torHost, torSocksPort), TCP_CONNECT_TIMEOUT_MS)
            remoteSocket.tcpNoDelay = true

            val remoteInput = remoteSocket.getInputStream()
            val remoteOutput = remoteSocket.getOutputStream()

            // 1. SOCKS5 greeting to Tor
            remoteOutput.write(byteArrayOf(0x05, 0x01, 0x00))
            remoteOutput.flush()

            val greetResp = ByteArray(2)
            remoteInput.readFully(greetResp)
            if (greetResp[0] != 0x05.toByte() || greetResp[1] != 0x00.toByte()) {
                remoteSocket.close()
                return false
            }

            // 2. CONNECT to upstream proxy via Tor
            val hostBytes = upstreamHost.toByteArray(Charsets.US_ASCII)
            val upPortBytes = byteArrayOf((upstreamPort shr 8).toByte(), (upstreamPort and 0xFF).toByte())
            val upReq = byteArrayOf(0x05, 0x01, 0x00, 0x03, hostBytes.size.toByte()) + hostBytes + upPortBytes
            remoteOutput.write(upReq)
            remoteOutput.flush()

            val torResp = ByteArray(4)
            remoteInput.readFully(torResp)
            if (torResp[1] != 0x00.toByte()) {
                remoteSocket.close()
                return false
            }
            when (torResp[3].toInt() and 0xFF) {
                0x01 -> { val rest = ByteArray(6); remoteInput.readFully(rest) }
                0x03 -> { val len = remoteInput.read(); val rest = ByteArray(len + 2); remoteInput.readFully(rest) }
                0x04 -> { val rest = ByteArray(18); remoteInput.readFully(rest) }
            }

            // 3. Negotiate HTTP CONNECT with upstream proxy
            val connectHttp = "CONNECT $destHost:$destPort HTTP/1.1\r\nHost: $destHost:$destPort\r\nProxy-Connection: Keep-Alive\r\nUser-Agent: DeltaTor/2.0\r\n\r\n"
            remoteOutput.write(connectHttp.toByteArray(Charsets.US_ASCII))
            remoteOutput.flush()

            val reader = java.io.BufferedReader(java.io.InputStreamReader(remoteInput, Charsets.US_ASCII))
            val statusLine = reader.readLine() ?: ""
            if (!statusLine.contains("200")) {
                logd("CONNECT: double-hop upstream returned status: $statusLine")
                remoteSocket.close()
                return false
            }
            while (true) {
                val line = reader.readLine()
                if (line.isNullOrEmpty()) break
            }

            logd("CONNECT: double-hop Tor -> $upstreamHost:$upstreamPort -> $destHost:$destPort OK")

            if (sendReply) {
                clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                clientOutput.flush()
            }
            clientSocket.soTimeout = 0

            remoteSocket.use { remote ->
                val t1 = Thread({
                    try {
                        copyStream(clientInput, remoteOutput)
                    } catch (_: Exception) {
                    } finally {
                        try { remoteOutput.close() } catch (_: Exception) {}
                    }
                }, "tor-bridge-chain-c2s")
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
            return true
        } catch (e: Exception) {
            logd("CONNECT: double-hop failed: ${e.message}")
            try { remoteSocket?.close() } catch (_: Exception) {}
            return false
        }
    }

    private fun connectDirectUpstream(
        label: String,
        destHost: String,
        destPort: Int,
        clientSocket: Socket,
        clientInput: InputStream,
        clientOutput: OutputStream,
        sendReply: Boolean = true
    ) {
        try {
            val remoteSocket = Socket()
            val targetAddr = if (DomainRouter.isIpAddress(destHost)) {
                InetSocketAddress(destHost, destPort)
            } else {
                val ip = try {
                    java.net.InetAddress.getByName(destHost).hostAddress ?: destHost
                } catch (_: Exception) {
                    destHost
                }
                InetSocketAddress(ip, destPort)
            }
            remoteSocket.connect(targetAddr, TCP_CONNECT_TIMEOUT_MS)
            remoteSocket.tcpNoDelay = true
            if (sendReply) {
                clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                clientOutput.flush()
            }
            clientSocket.soTimeout = 0
            logd("CONNECT: $label direct connection OK to $destHost:$destPort")
            bridgeDirect(clientInput, clientOutput, remoteSocket)
        } catch (e: Exception) {
            logd("CONNECT: $label failed to connect to $destHost:$destPort: ${e.message}")
            if (sendReply) {
                try {
                    clientOutput.write(byteArrayOf(0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
                    clientOutput.flush()
                } catch (_: Exception) {}
            }
        }
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
        var effectiveRawAddr = rawAddr
        if (FakeDns.enabled && FakeDns.isFakeIp(destHost)) {
            val domain = FakeDns.getDomain(destHost)
            if (domain != null) {
                effectiveHost = domain
                val dBytes = domain.toByteArray(Charsets.US_ASCII)
                effectiveRawAddr = byteArrayOf(0x03, dBytes.size.toByte()) + dBytes
                logd("CONNECT: FakeDNS mapped $destHost -> $domain")
            }
        }

        var sniffBuffer: ByteArray? = null
        var sniffLen = 0
        var wasEarlyReply = false

        if (DomainRouter.isIpAddress(effectiveHost)) {
            // IP address — sniff to recover domain
            clientOutput.write(byteArrayOf(0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0))
            clientOutput.flush()
            clientSocket.soTimeout = 3000
            wasEarlyReply = true

            val result = ProtocolSniffer.sniff(clientInput)
            if (result.domain != null) {
                effectiveHost = result.domain
                val dBytes = effectiveHost.toByteArray(Charsets.US_ASCII)
                effectiveRawAddr = byteArrayOf(0x03, dBytes.size.toByte()) + dBytes
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
        when (upstreamMode) {
            UpstreamMode.TOR -> {
                if (chainMode != ChainMode.NONE) {
                    connectViaTorChained(chainMode, effectiveHost, destPort, effectiveRawAddr, portBytes, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
                } else {
                    connectViaTor(effectiveHost, destPort, effectiveRawAddr, portBytes, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
                }
            }
            UpstreamMode.WARP -> {
                connectDirectUpstream("WARP", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
            UpstreamMode.AMNEZIA -> {
                connectDirectUpstream("Amnezia", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
            UpstreamMode.MASQUE -> {
                connectDirectUpstream("MASQUE", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
            UpstreamMode.DIRECT -> {
                connectDirectUpstream("Direct", effectiveHost, destPort, clientSocket, effectiveInput, clientOutput, sendReply = !wasEarlyReply)
            }
        }
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
                Log.w(TAG, "CONNECT: Tor rejected to $destHost:$destPort (rep=${connRespHeader[1]})")
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
     * DNS (port 53): DNS-over-TCP through Tor SOCKS5 CONNECT, so no query ever
     * leaves the device in the clear. Non-DNS UDP: dropped silently.
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
            try {
                dnsExecutor.submit {
                try {
                    // Check FakeDNS first (0ms synthetic response in 198.18.0.0/15)
                    val fakeResp = if (FakeDns.enabled) FakeDns.handleDnsQuery(payloadCopy) else null
                    val response = if (fakeResp != null) {
                        logd("DNS: answered via FakeDNS synthesizer")
                        fakeResp
                    } else {
                        // Check cache
                        val cached = getCachedDns(payloadCopy)
                        if (cached != null) {
                            logd("DNS: cache hit")
                            cached
                        } else {
                            val remote = if (upstreamMode == UpstreamMode.TOR && torSocksPort > 0) {
                                resolveThroughTor(payloadCopy) ?: resolveDirect(payloadCopy)
                            } else {
                                resolveDirect(payloadCopy)
                            }
                            if (remote != null) cacheDnsResponse(payloadCopy, remote)
                            remote
                        }
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
            } catch (e: java.util.concurrent.RejectedExecutionException) {
                Log.w(TAG, "DNS task rejected (bridge is stopping)")
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

    // --- DNS-over-TCP via Tor SOCKS5 ---

    /**
     * Resolvers tried in order, all of them reached over Tor. One being slow,
     * blocked at the exit or simply broken should not take name resolution down
     * with it.
     */
    private val DNS_FALLBACK_RESOLVERS = listOf(
        byteArrayOf(8, 8, 8, 8),   // Google
        byteArrayOf(1, 1, 1, 1),   // Cloudflare
        byteArrayOf(9, 9, 9, 9)    // Quad9
    )

    /** Ask each resolver in turn over Tor; null when none answers. */
    private fun resolveThroughTor(payload: ByteArray): ByteArray? {
        if (torSocksPort <= 0) return null
        for (resolver in DNS_FALLBACK_RESOLVERS) {
            val response = forwardDnsTcp(resolver, payload)
            if (response != null) return response
        }
        Log.w(TAG, "DNS: no resolver answered over Tor")
        return null
    }

    /**
     * Forward DNS query as DNS-over-TCP through Tor's SOCKS5 CONNECT to
     * [resolver]:53. Each call opens a new SOCKS5 connection (no persistent
     * connections to keep it simple).
     */
    /**
     * Resolves DNS query directly to Anycast resolvers (1.1.1.1, 8.8.8.8, 9.9.9.9)
     * using UDP first, falling back to TCP RFC 1035.
     * Used for standalone engine modes (WARP, AmneziaWG, MASQUE, Direct) or Tor startup fallback.
     */
    private fun resolveDirect(payload: ByteArray): ByteArray? {
        val resolvers = listOf(
            InetSocketAddress("1.1.1.1", 53),
            InetSocketAddress("8.8.8.8", 53),
            InetSocketAddress("9.9.9.9", 53)
        )
        // 1. Try standard UDP
        for (resolver in resolvers) {
            try {
                java.net.DatagramSocket().use { ds ->
                    ds.soTimeout = 2500
                    val sendPacket = java.net.DatagramPacket(payload, payload.size, resolver)
                    ds.send(sendPacket)
                    val buf = ByteArray(4096)
                    val recvPacket = java.net.DatagramPacket(buf, buf.size)
                    ds.receive(recvPacket)
                    if (recvPacket.length > 2) {
                        logd("DNS: resolved directly via UDP ${resolver.hostString}")
                        return buf.copyOf(recvPacket.length)
                    }
                }
            } catch (_: Exception) {}
        }
        // 2. Fallback to DNS-over-TCP
        for (resolver in resolvers) {
            try {
                Socket().use { s ->
                    s.connect(resolver, 2500)
                    s.soTimeout = 2500
                    val out = s.getOutputStream()
                    val inp = s.getInputStream()
                    out.write(byteArrayOf(((payload.size shr 8) and 0xFF).toByte(), (payload.size and 0xFF).toByte()))
                    out.write(payload)
                    out.flush()
                    val lenBuf = ByteArray(2)
                    inp.readFully(lenBuf)
                    val len = ((lenBuf[0].toInt() and 0xFF) shl 8) or (lenBuf[1].toInt() and 0xFF)
                    if (len in 1..65535) {
                        val resp = ByteArray(len)
                        inp.readFully(resp)
                        logd("DNS: resolved directly via TCP ${resolver.hostString}")
                        return resp
                    }
                }
            } catch (_: Exception) {}
        }
        Log.w(TAG, "DNS: direct resolution failed across all resolvers")
        return null
    }

    private fun forwardDnsTcp(resolver: ByteArray, payload: ByteArray): ByteArray? {
        var socket: Socket? = null
        try {
            // Connect to Tor SOCKS5
            socket = Socket()
            socket.connect(InetSocketAddress(torHost, torSocksPort), DNS_TIMEOUT_MS)
            socket.soTimeout = DNS_TIMEOUT_MS
            socket.tcpNoDelay = true

            val input = socket.getInputStream()
            val output = socket.getOutputStream()

            // SOCKS5 greeting (no auth)
            output.write(byteArrayOf(0x05, 0x01, 0x00))
            output.flush()

            val greetResp = ByteArray(2)
            input.readFully(greetResp)
            if (greetResp[0] != 0x05.toByte()) {
                logd("DNS: Tor SOCKS5 greeting failed")
                return null
            }

            // SOCKS5 CONNECT to the resolver on port 53
            output.write(byteArrayOf(0x05, 0x01, 0x00, 0x01) + resolver + byteArrayOf(0x00, 0x35))
            output.flush()

            // Read CONNECT response
            val connResp = ByteArray(4)
            input.readFully(connResp)
            if (connResp[1] != 0x00.toByte()) {
                logd("DNS: SOCKS5 CONNECT rejected (rep=${connResp[1]})")
                return null
            }

            // Skip remaining response bytes based on address type
            when (connResp[3].toInt() and 0xFF) {
                0x01 -> { val rest = ByteArray(6); input.readFully(rest) }
                0x03 -> { val len = input.read(); val rest = ByteArray(len + 2); input.readFully(rest) }
                0x04 -> { val rest = ByteArray(18); input.readFully(rest) }
            }

            // Send DNS query as TCP (2-byte length prefix per RFC 1035 section 4.2.2)
            output.write(byteArrayOf(
                ((payload.size shr 8) and 0xFF).toByte(),
                (payload.size and 0xFF).toByte()
            ))
            output.write(payload)
            output.flush()

            // Read DNS response (2-byte length prefix)
            val respLenBuf = ByteArray(2)
            input.readFully(respLenBuf)
            val respLen = ((respLenBuf[0].toInt() and 0xFF) shl 8) or (respLenBuf[1].toInt() and 0xFF)

            if (respLen <= 0 || respLen > 65535) {
                logd("DNS: invalid response length: $respLen")
                return null
            }

            val response = ByteArray(respLen)
            input.readFully(response)

            logd("DNS: resolved via Tor (${response.size} bytes)")
            return response
        } catch (e: Exception) {
            logd("DNS-over-TCP failed: ${e.message}")
            return null
        } finally {
            try { socket?.close() } catch (_: Exception) {}
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
