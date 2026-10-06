package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * A single Tor client instance bound to one transport.
 *
 * Used by [ParallelTorManager] to race vanilla / obfs4 / webtunnel against
 * each other. Each runner owns its own Tor data dir, lyrebird (obfs4proxy)
 * PT process and SOCKS5 port, so multiple transports can bootstrapped in
 * parallel.
 *
 * Transport handling:
 * - "vanilla": bridge lines are `IP:PORT FP` (no pluggable transport plugin).
 * - "obfs4" / "webtunnel": a managed lyrebird (libobfs4proxy.so) process is
 *   started first; Tor is configured via `ClientTransportPlugin socks5 <addr>`
 *   pointing at the PT's SOCKS5 listener.
 */
class TorRunner(
    private val context: Context,
    val name: String,
    val torSocksPort: Int,
    private val bridgeLines: String,
    private val listenHost: String = "127.0.0.1"
) {
    private val tag = "TorRunner[$name]"

    private val dataDir = File(context.filesDir, "tor_data_$name")
    private val ptStateDir = File(dataDir, "pt_state")

    @Volatile private var torProcess: Process? = null
    @Volatile private var lyrebirdProcess: Process? = null
    private val lyrebirdCmethods = mutableMapOf<String, String>()

    /** The loopback control port Tor binds when exit countries are selected. */
    @Volatile private var controlPort = 0
    private val exitApplied = AtomicBoolean(false)
    private val exitAppliedLatch = CountDownLatch(1)

    val bootstrapPercent = AtomicInteger(0)
    @Volatile var ready = false
    @Volatile var failed: String? = null
    @Volatile var started = false

    /**
     * True once [stop] has run and every process it owned was seen to exit, so
     * a caller can report a real "the core is gone" instead of "we asked it to
     * go". False before the first stop, and false if a process outlived SIGKILL.
     */
    @Volatile var confirmedStopped = false

    fun isRunning(): Boolean {
        val lyrebirdOk = lyrebirdProcess == null || lyrebirdProcess?.isAlive == true
        return torProcess?.isAlive == true && lyrebirdOk
    }

    fun isReady(): Boolean = ready && torProcess?.isAlive == true

    fun progress(): Int = bootstrapPercent.get()

    /** Blocks until the post-bootstrap exit steering finished (or timed out). */
    fun awaitExitApplied(timeoutMs: Long): Boolean =
        exitAppliedLatch.await(timeoutMs, TimeUnit.MILLISECONDS)

    /**
     * Once this runner is bootstrapped, push the selected exit countries onto the
     * running Tor over its control port. Runs once per session on a daemon thread
     * so it can never block the log reader. Every step is traced under its own
     * "ExitNode" log section so a device-side failure is visible in the app log.
     */
    private fun applyExitNodesLater() {
        val ccs = ExitNodes.currentCodes()
            .map { it.trim().uppercase() }
            .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
            .distinct()
        if (ccs.isEmpty()) return
        if (!exitApplied.compareAndSet(false, true)) return
        val port = controlPort
        if (port <= 0) return
        Thread({
            var ok = false
            try {
                ok = applyExitNodesViaControl(port, ccs)
            } catch (e: Exception) {
                Log.w(TAG_EXIT, "[$name] could not apply via control port $port: ${e.message}")
            }
            exitAppliedLatch.countDown()
            Log.i(TAG_EXIT, "[$name] exit nodes ${if (ok) "ACTIVE" else "NOT applied"} (${ccs.joinToString(",")}; StrictNodes 0)")
        }, "$name-exit-apply").also { it.isDaemon = true; it.start() }
    }

    /**
     * The control exchange itself. Returns true when the exit rules took effect.
     *
     * The template this app ships with keeps a 24h [MaxCircuitDirtiness] and the
     * fork's Conflux enabled. Both fight the live switch: the first circuit of
     * the session was built without an exit restriction, so all traffic would
     * ride that single circuit's original (wrong) country for a whole day, and
     * Conflux sets rebuilt after the switch keep relinking unrestricted legs.
     * The present values are therefore retired (10 min turnover, Conflux off)
     * and a fresh identity is requested so the next streams leave through the
     * selected countries. Any line the binary rejects is left alone, traced, and
     * the session continues with the exit list still in force.
     */
    private fun applyExitNodesViaControl(port: Int, ccs: List<String>): Boolean {
        val exitValue = ccs.joinToString(",") { "{$it}" }
        Log.i(TAG_EXIT, "=== EXIT NODE |> $name |> ${ccs.joinToString(",")} ===")
        Log.i(TAG_EXIT, "[$name] geoip: built earlier for ${ccs.joinToString(",")}")
        Log.i(TAG_EXIT, "[$name] control port: $listenHost:$port")
        return sendControl(
            port,
            listOf(
                ControlCommand("AUTHENTICATE", optional = false),
                ControlCommand("SETCONF ExitNodes=\"$exitValue\"", optional = false),
                ControlCommand("SETCONF StrictNodes=0", optional = false),
                ControlCommand("SETCONF MaxCircuitDirtiness=600", optional = true),
                ControlCommand("SETCONF ConfluxEnabled=0", optional = true),
                ControlCommand("SETCONF CircuitBuildTimeout=180", optional = true),
                ControlCommand("SETCONF LearnCircuitBuildTimeout=1", optional = true),
                ControlCommand("SIGNAL NEWNYM", optional = true),
                ControlCommand("GETCONF ExitNodes", optional = false),
                ControlCommand("GETCONF StrictNodes", optional = false)
            )
        )
    }

    private class ControlCommand(val line: String, val optional: Boolean)

    /**
     * Send a short control exchange to a bootstrapped Tor. Logs one line per
     * command (command + final status) under the ExitNode section; optional
     * commands that fail are waived with a warning, mandatory ones throw.
     */
    private fun sendControl(port: Int, commands: List<ControlCommand>): Boolean {
        val socket = java.net.Socket()
        try {
            socket.connect(java.net.InetSocketAddress("127.0.0.1", port), 4_000)
            socket.soTimeout = 4_000
            val writer = socket.getOutputStream()
            val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.ISO_8859_1))
            for (command in commands) {
                writer.write(command.line.toByteArray(Charsets.ISO_8859_1))
                writer.write("\r\n".toByteArray(Charsets.ISO_8859_1))
                writer.flush()
                val reply = readReply(reader)
                val ok = reply.isNotEmpty() && reply.last().startsWith("250")
                if (!ok) {
                    if (command.optional) {
                        Log.w(TAG_EXIT, "[$name] ${command.line} -> ${reply.joinToString(" / ")} (ignored)")
                        continue
                    }
                    throw RuntimeException("${command.line} -> ${reply.joinToString(" / ")}")
                }
                Log.i(TAG_EXIT, "[$name] ${command.line} -> ${reply.last()}")
            }
            return true
        } finally {
            runCatching { socket.close() }
        }
    }

    /**
     * Read one complete control reply. Single-line replies come back as one
     * line; multi-line "250-..." blocks end at their "250 ..." terminator and
     * "250+..." (data) blocks consume the raw data lines up to the "." line and
     * the closing "250 ..." line.
     */
    private fun readReply(reader: BufferedReader): List<String> {
        val out = ArrayList<String>()
        var line = reader.readLine() ?: return out
        out.add(line)
        if (line.startsWith("250+")) {
            // Raw data follows: read until the standalone "." terminator.
            while (true) {
                val data = reader.readLine() ?: break
                if (data == ".") break
                out.add(data)
            }
            reader.readLine()?.let { out.add(it) } // closing status line
        } else {
            while (line.startsWith("250-")) {
                line = reader.readLine() ?: break
                out.add(line)
            }
        }
        return out
    }

    /**
     * A free loopback port for the control listener. ServerSocket(0) picks one
     * from the ephemeral range; the small re-bind race is acceptable here and a
     * runner that wins only ever needs it after its own bootstrap.
     */
    private fun freeEphemeralPort(): Int = java.net.ServerSocket(0).use { it.localPort }

    // Ring buffer of this runner's own log lines, so a dead process can be
    // explained after the fact (Tor's own words, not a generic "process exited").
    private val recentLog = ArrayDeque<String>()

    private fun noteLog(message: String) {
        synchronized(recentLog) {
            recentLog.addLast(message)
            while (recentLog.size > 60) recentLog.removeFirst()
        }
    }

    /**
     * One-line reason this runner is not going to win: the most informative
     * line Tor or lyrebird printed, preferring errors/warnings over the rest.
     */
    fun failureSummary(): String {
        val lines = synchronized(recentLog) { recentLog.toList() }
        if (lines.isEmpty()) return "process exited (no output captured)"
        val meaningful = lines.filter { line ->
            val l = line.lowercase()
            l.contains("error") || l.contains("failed") || l.contains("fatal") ||
                l.contains("warn") || l.contains("could not") || l.contains("unable") ||
                l.contains("refused") || l.contains("no such") || l.contains("not found") ||
                l.contains("unrecognized") || l.contains("invalid") || l.contains("timeout")
        }
        val detail = (meaningful.ifEmpty { lines }).last().trim()
        val exit = torProcess
            ?.takeIf { !it.isAlive }
            ?.let { " (exit ${runCatching { it.exitValue() }.getOrDefault(-1)})" }
            .orEmpty()
        return "process exited$exit \u00b7 ${detail.take(220)}"
    }

    /** Everything this runner has logged during the current session (for the log UI). */
    fun logLines(): List<String> = synchronized(recentLog) { recentLog.toList() }

    /**
     * Fingerprints of the bridges that actually worked for this run: Tor only
     * logs a bridge descriptor once the pluggable-transport handshake and the
     * descriptor download both succeeded, which makes it the one reliable
     * "this bridge is alive" signal in the log.
     */
    private val healthyBridges = LinkedHashSet<String>()

    fun healthyBridges(): Set<String> = synchronized(healthyBridges) { healthyBridges.toSet() }

    // Matches: new bridge descriptor 'NAME' (cached): $FINGERPRINT~NAME [...]
    // and the "(fresh): $" variant. Mirrors the Windows client's log scan.
    private fun noteBridgeDescriptor(line: String) {
        if (!line.contains("bridge descriptor", ignoreCase = true)) return
        var i = line.indexOf(CACHED_MARKER)
        if (i < 0) {
            i = line.indexOf(FRESH_MARKER)
            if (i < 0) return
            i += FRESH_MARKER.length
        } else {
            i += CACHED_MARKER.length
        }
        // Hex only, and all of it. A partial or non-hex fingerprint is worse than
        // none: it is stored, it counts towards the pool shown on the stats screen,
        // and it matches no line in any list, so it can never become a bridge. The
        // scan therefore stops at the first character that is not hex rather than
        // accepting whatever is alphanumeric.
        val hex = StringBuilder(FINGERPRINT_HEX_LENGTH)
        while (i < line.length && line[i] != '~' && hex.length < FINGERPRINT_HEX_LENGTH) {
            val c = line[i]
            if (c !in '0'..'9' && c !in 'a'..'f' && c !in 'A'..'F') break
            hex.append(c)
            i++
        }
        // Lower-cased on the way in, because the lists are lower-cased and a
        // fingerprint is the same fingerprint in either case. Matching them
        // case-sensitively is a pool that fills up and never matches anything.
        val fp = hex.toString().lowercase()
        if (fp.length == FINGERPRINT_HEX_LENGTH) {
            synchronized(healthyBridges) { healthyBridges.add(fp) }
        }
    }

    /**
     * Start the PT (if needed) and the Tor process for this transport.
     * Bridge lines are capped to [MAX_BRIDGE_LINES], taken from the end of a list
     * that has already been shuffled, so which lines are used differs per attempt.
     */
    fun start(): Result<Unit> {
        stop()
        bootstrapPercent.set(0)
        controlPort = 0
        exitApplied.set(false)
        while (exitAppliedLatch.count > 0) exitAppliedLatch.countDown()
        ready = false
        failed = null
        started = true
        synchronized(recentLog) { recentLog.clear() }
        synchronized(healthyBridges) { healthyBridges.clear() }

        try {
            val rawLines = bridgeLines.lines()
                .map { it.trim() }
                .filter { it.isNotBlank() && !it.startsWith("#") }
                .map { if (it.lowercase().startsWith("bridge ")) it.substring(7).trim() else it }
            // Tor aborts on the FIRST bad Bridge line, so a single malformed entry
            // from a collector would take the whole transport down. Drop them here
            // and say so, instead of losing the runner.
            val (wellFormed, malformed) = rawLines.partition { isValidBridgeLine(it) }
            if (malformed.isNotEmpty()) {
                Log.w(tag, "dropped ${malformed.size} malformed bridge line(s), first: ${malformed.first().take(70)}")
            }
            val cleanLines = wellFormed.take(MAX_BRIDGE_LINES)

            val isDirect = name == "direct"
            if (cleanLines.isEmpty() && !isDirect) {
                return Result.failure(RuntimeException("$tag: no bridge lines available"))
            }

            // `vanilla-memory` is the vanilla transport too, hence startsWith.
            val isVanilla = name.startsWith(ParallelTorManager.TRANSPORT_VANILLA) || isDirect
            // Only real pluggable-transport names count. The memory runner mixes
            // plain `ip:port fp` lines with prefixed ones, and a bare address as
            // the first token must not be mistaken for a CMETHOD.
            val transports = if (isVanilla) mutableListOf<String>() else
                cleanLines.mapNotNull { it.split(WHITESPACE).firstOrNull()?.lowercase() }
                    .filter { it in PLUGGABLE_TRANSPORTS }
                    .distinct()
                    .toMutableList()

            dataDir.mkdirs()
            ptStateDir.mkdirs()
            listOf("state", "lock").forEach { f ->
                val file = File(dataDir, f)
                if (file.exists()) file.delete()
            }

            // The lines Tor is finally given. Narrowed below when lyrebird turns
            // out not to speak every transport the list asked for.
            var usableLines = cleanLines

            if (transports.isNotEmpty()) {
                val ptBinary = getObfs4proxyPath()
                    ?: return Result.failure(RuntimeException("$tag: lyrebird (obfs4proxy) binary not found"))
                val result = startLyrebird(ptBinary, transports)
                if (result.isFailure) return result
                val missing = transports.filter { it !in lyrebirdCmethods }
                if (missing.isNotEmpty()) {
                    // lyrebird here is obfs4proxy: it serves obfs4, webtunnel and
                    // meek-lite, and it has no snowflake. A list that mixes a
                    // transport it cannot serve with ones it can is still worth
                    // running on the ones it can, so the lines that need the missing
                    // transport are dropped and the runner goes on. Only a list that
                    // needs nothing else is a real failure.
                    Log.w(tag, "Lyrebird cannot serve $missing; dropping those bridge line(s)")
                    transports.removeAll(missing.toSet())
                    usableLines = cleanLines.filterNot { line ->
                        line.split(WHITESPACE).firstOrNull()?.lowercase() in missing
                    }
                    if (usableLines.isEmpty() && !isDirect) {
                        stop()
                        return Result.failure(
                            RuntimeException("$tag: every bridge line needs $missing")
                        )
                    }
                }
            }

            prepareGeoIp()

            val torrcPath = writeTorrc(usableLines, isVanilla, isDirect)
            val torBinary = context.applicationInfo.nativeLibraryDir + "/libtor.so"
            if (!File(torBinary).exists()) {
                return Result.failure(RuntimeException("$tag: Tor binary not found at $torBinary"))
            }

            val pb = ProcessBuilder(torBinary, "-f", torrcPath)
            pb.redirectErrorStream(true)
            pb.environment()["HOME"] = dataDir.absolutePath
            val process = pb.start()
            torProcess = process

            Thread({
                try {
                    val reader = BufferedReader(InputStreamReader(process.inputStream))
                    var line: String?
                    while (reader.readLine().also { line = it } != null) {
                        noteLog("Tor: $line")
                        val up = line!!
                        Log.d(tag, up)
                        noteBridgeDescriptor(up)
                        val match = Regex("Bootstrapped (\\d+)%").find(up)
                        if (match != null) {
                            val pct = match.groupValues[1].toInt()
                            bootstrapPercent.set(pct)
                            Log.i(tag, "Bootstrap: $pct%")
                            if (pct >= 100) {
                                ready = true
                                applyExitNodesLater()
                            }
                        }
                    }
                } catch (e: Exception) {
                    if (torProcess != null) {
                        Log.w(tag, "Tor output reader error: ${e.message}")
                    }
                }
            }, "$name-tor-output").also { it.isDaemon = true; it.start() }

            Log.i(tag, "Started Tor on $listenHost:$torSocksPort ($name, ${transports.joinToString(",") ?: "vanilla"} transport)")
            return Result.success(Unit)
        } catch (e: Exception) {
            Log.e(tag, "Start failed", e)
            stop()
            return Result.failure(e)
        }
    }

    // --- Lyrebird managed transport ---

    /**
     * Launch lyrebird as a managed transport process and wait for CMETHOD
     * registration. Packaged with TransportProtocol (Tor's PT spec), it can
     * provide obfs4, webtunnel and meek_lite transports.
     */
    private fun startLyrebird(ptBinaryPath: String, transports: List<String>): Result<Unit> {
        lyrebirdCmethods.clear()

        Log.i(tag, "Launching lyrebird for transports: ${transports.joinToString(",")}")

        val pb = ProcessBuilder(ptBinaryPath)
        pb.redirectErrorStream(false)
        pb.environment().apply {
            put("TOR_PT_MANAGED_TRANSPORT_VER", "1")
            put("TOR_PT_CLIENT_TRANSPORTS", transports.joinToString(","))
            put("TOR_PT_STATE_LOCATION", ptStateDir.absolutePath + "/")
            put("TOR_PT_EXIT_ON_STDIN_CLOSE", "1")
        }

        val process: Process
        try {
            process = pb.start()
        } catch (e: Exception) {
            Log.e(tag, "Failed to launch lyrebird: ${e.message}")
            return Result.failure(RuntimeException("$tag: failed to launch lyrebird: ${e.message}"))
        }
        lyrebirdProcess = process

        Thread({
            try {
                val reader = BufferedReader(InputStreamReader(process.errorStream))
                var line: String?
                while (reader.readLine().also { line = it } != null) {
                    val l = "lyrebird stderr: $line"
                    noteLog(l)
                    Log.d(tag, l)
                }
            } catch (_: Exception) {}
        }, "$name-lyrebird-stderr").also { it.isDaemon = true; it.start() }

        val cmethodsDone = CountDownLatch(1)
        var protocolError: String? = null

        Thread({
            try {
                val reader = BufferedReader(InputStreamReader(process.inputStream))
                var line: String?
                while (reader.readLine().also { line = it } != null) {
                    val l = line!!.trim()
                    noteLog("lyrebird PT: $l")
                    Log.d(tag, "Lyrebird PT: $l")
                    when {
                        l.startsWith("VERSION ") -> Log.i(tag, "Lyrebird protocol: $l")
                        l.startsWith("CMETHOD ") -> {
                            val parts = l.split("\\s+".toRegex())
                            if (parts.size >= 4) {
                                val m = parts[1]
                                val addr = parts[3]
                                lyrebirdCmethods[m] = addr
                                Log.i(tag, "Lyrebird registered: $m at $addr")
                            }
                        }
                        l.startsWith("CMETHOD-ERROR ") -> Log.e(tag, "Lyrebird transport error: $l")
                        l == "CMETHODS DONE" -> {
                            Log.i(tag, "Lyrebird: all transports registered")
                            cmethodsDone.countDown()
                        }
                        l.startsWith("ENV-ERROR ") -> {
                            protocolError = l
                            cmethodsDone.countDown()
                        }
                        l.startsWith("VERSION-ERROR ") -> {
                            protocolError = l
                            cmethodsDone.countDown()
                        }
                    }
                }
            } catch (e: Exception) {
                if (lyrebirdProcess != null) {
                    Log.w(tag, "Lyrebird stdout reader error: ${e.message}")
                }
            }
            cmethodsDone.countDown()
        }, "$name-lyrebird-stdout").also { it.isDaemon = true; it.start() }

        val success = cmethodsDone.await(10, TimeUnit.SECONDS)
        if (!success) {
            Log.e(tag, "Lyrebird timed out waiting for CMETHODS DONE")
            stop()
            return Result.failure(RuntimeException("$tag: lyrebird timed out during PT protocol setup"))
        }
        if (protocolError != null) {
            stop()
            return Result.failure(RuntimeException("$tag: lyrebird protocol error: $protocolError"))
        }
        if (!process.isAlive) {
            val exitCode = process.exitValue()
            stop()
            return Result.failure(RuntimeException("$tag: lyrebird exited with code $exitCode"))
        }
        return Result.success(Unit)
    }

    // --- torrc ---

    private fun writeTorrc(cleanLines: List<String>, isVanilla: Boolean, isDirect: Boolean = false): String {
        // Filtered to real transport names for the same reason start() does: a
        // plain `ip:port fp` line has an address as its first token, and treating
        // that as a transport name logged one bogus "no CMETHOD" warning per
        // bridge. A mixed list is hundreds of lines, so it was hundreds of them.
        val transports = cleanLines.map { it.split("\\s+".toRegex()).firstOrNull()?.lowercase() ?: "" }
            .filter { it in PLUGGABLE_TRANSPORTS }
            .distinct()

        val torrcFile = File(dataDir, "torrc")
        val common = buildString {
            appendLine("SocksPort $listenHost:$torSocksPort")
            appendLine("DataDirectory ${dataDir.absolutePath}")
            appendLine("UseBridges ${if (isDirect) 0 else 1}")
            val geoipFile = File(dataDir, "geoip")
            val geoip6File = File(dataDir, "geoip6")
            if (geoipFile.exists()) appendLine("GeoIPFile ${geoipFile.absolutePath}")
            if (geoip6File.exists()) appendLine("GeoIPv6File ${geoip6File.absolutePath}")
            appendLine("Log info stdout")
            appendLine("KeepalivePeriod 30")
            appendLine("ClientUseIPv4 1")
            appendLine("ClientUseIPv6 1")
            appendLine("ClientPreferIPv6ORPort auto")
            appendLine("DormantClientTimeout 2419200")
            appendLine("ClientBootstrapConsensusAuthorityDownloadInitialDelay 0")
            appendLine("ReducedConnectionPadding 0")
        }.trim()

        val pluginDirectives = StringBuilder()
        if (!isVanilla) {
            for (transport in transports) {
                val addr = lyrebirdCmethods[transport]
                if (addr != null) {
                    pluginDirectives.appendLine("ClientTransportPlugin $transport socks5 $addr")
                } else {
                    Log.w(tag, "No lyrebird CMETHOD for transport: $transport")
                }
            }
        }

        val bridgeDirectives = StringBuilder()
        for (line in cleanLines) {
            bridgeDirectives.appendLine("Bridge $line")
        }

        // Exit-country steering is applied AFTER bootstrap via the control port
        // (see applyExitNodesLater). Bootstrapping with ``ExitNodes`` in the
        // torrc makes the initial microdescriptor phase much slower, because Tor
        // only counts descriptors of the chosen countries towards ``probably``
        // having enough directory info; on a censored net with a mostly dead
        // bridge pool that extra delay is where "never connects" comes from. So
        // the first boot is unrestricted (fast), and the country rules are
        // pushed in once the runner is already at 100%: Tor then steers new
        // circuits into the selected countries without the slow cold start.
        val exitCodes = ExitNodes.currentCodes()
            .map { it.trim().uppercase() }
            .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
            .distinct()
        if (exitCodes.isNotEmpty()) {
            controlPort = freeEphemeralPort()
        }

        val templateLines = TorrcSettings.templateLines()
        val torrcContent = "$common\n${templateLines.joinToString("\n")}\n${pluginDirectives.toString().trim()}\n${bridgeDirectives.toString().trim()}\n" +
            (if (exitCodes.isNotEmpty()) "ControlPort $listenHost:$controlPort\n" else "")
        torrcFile.writeText(torrcContent)
        try {
            File(context.filesDir, "tor_last.torrc").writeText(torrcContent)
        } catch (_: Exception) {}

        Log.d(tag, "--- Generated torrc ($name) ---")
        torrcContent.lines().forEach { line ->
            if (line.isNotBlank()) Log.d(tag, "torrc: $line")
        }
        Log.d(tag, "--- End torrc ($name) ---")
        return torrcFile.absolutePath
    }

    // --- Helpers ---

    private fun getObfs4proxyPath(): String? {
        val binaryPath = context.applicationInfo.nativeLibraryDir + "/libobfs4proxy.so"
        return if (File(binaryPath).exists()) binaryPath else null
    }

    /**
     * Tor resolves an `ExitNodes {cc}` rule only through its geoip database, and the
     * bundled binary has none, so the database is generated here from the range
     * table the APK already carries. Without it the selected countries are ignored
     * without a word, so say so instead of pretending the exit is pinned.
     */
    private fun prepareGeoIp() {
        val codes = ExitNodes.currentCodes()
            .map { it.trim().uppercase() }
            .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
            .distinct()
        if (codes.isEmpty()) {
            // No country chosen: drop a stale database so nothing points at it.
            GeoIpFile.discard(dataDir)
            return
        }
        val built = GeoIpFile.ensure(context, dataDir, codes.toSet())
        if (built) {
            Log.i(TAG_EXIT, "[$name] geoip ready for ${codes.joinToString(",")} (Tor can resolve the exit countries)")
        } else {
            Log.w(TAG_EXIT, "[$name] geoip NOT built: Tor cannot resolve ${codes.joinToString(",")} and will pick any exit")
            Log.w(tag, "no geoip database: the exit country cannot be enforced, Tor will pick any exit")
        }
    }

    /**
     * Stop the Tor and lyrebird processes for this instance.
     *
     * This blocks until the processes are really gone: a listening socket is only
     * released when its process exits, which lags destroy()/destroyForcibly().
     * Callers that immediately rebind the same port depend on that guarantee,
     * so a stubborn process is reported instead of being left behind.
     */
    @Synchronized
    fun stop() {
        val tor = torProcess
        val bird = lyrebirdProcess
        torProcess = null
        lyrebirdProcess = null

        val torDead = terminate(tor, "Tor")
        val birdDead = terminate(bird, "lyrebird")
        confirmedStopped = (tor == null || torDead) && (bird == null || birdDead)

        lyrebirdCmethods.clear()
        ready = false
    }

    /** Returns true only when the process is confirmed gone, SIGKILL included. */
    private fun terminate(p: Process?, what: String): Boolean {
        if (p == null) return true
        try {
            p.destroy()
            if (!p.waitFor(TERMINATE_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                Log.w(tag, "$what ignored SIGTERM, forcing kill")
                p.destroyForcibly()
                if (!p.waitFor(TERMINATE_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                    Log.e(tag, "$what still alive after SIGKILL: ${describe(p)}")
                    return false
                }
            }
            return true
        } catch (e: Exception) {
            Log.e(tag, "Error stopping $what", e)
            try { p.destroyForcibly() } catch (_: Exception) {}
            return !p.isAlive
        }
    }

    private fun describe(p: Process): String = runCatching { p.toString() }.getOrDefault("pid ?")

    /**
     * True when Tor will accept this as a `Bridge` line.
     *
     * Tor refuses the whole configuration on the first bridge it cannot parse, and
     * the usual offender is a fingerprint that is not exactly 40 hex characters, so
     * that is checked here before anything reaches torrc. The shape is either
     * `addr:port FINGERPRINT ...` (vanilla) or
     * `obfs4|webtunnel|snowflake|meek addr:port FINGERPRINT ...`.
     */
    private fun isValidBridgeLine(line: String): Boolean {
        val parts = line.split(WHITESPACE).filter { it.isNotBlank() }
        if (parts.size < 2) return false
        val fpIndex = if (parts[0].lowercase() in PLUGGABLE_TRANSPORTS) 2 else 1
        if (parts.size <= fpIndex) return false
        val addr = parts[fpIndex - 1]
        if (!addr.contains(':') || addr.startsWith(":")) return false
        return isFingerprint(parts[fpIndex])
    }

    /** A bridge identity digest is always 40 hex characters. */
    private fun isFingerprint(token: String): Boolean =
        token.length == FINGERPRINT_HEX_LENGTH &&
            token.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

    companion object {
        /**
         * How many bridge lines one runner is given.
         *
         * Every attempt draws this many out of a shuffled list, so it is a slice of
         * the whole list rather than the head of it, and raising it costs time
         * rather than accuracy: Tor tries them in order and a blocked one costs a
         * timeout each. It sits under [BridgeMemory.MAX_PER_TRANSPORT] so that a
         * pool can still fill two attempts' worth, and the two are what they are
         * because the pool is the knowledge and this is one read of it.
         */
        private const val MAX_BRIDGE_LINES = 150
        private const val FINGERPRINT_HEX_LENGTH = 40
        private const val TERMINATE_TIMEOUT_MS = 2_000L
        private const val TAG_EXIT = "ExitNode"
        private const val CACHED_MARKER = "(cached): \$"
        private const val FRESH_MARKER = "(fresh): \$"
        private val WHITESPACE = Regex("\\s+")
        private val PLUGGABLE_TRANSPORTS = setOf("obfs4", "webtunnel", "snowflake", "meek_lite", "meek")
    }
}