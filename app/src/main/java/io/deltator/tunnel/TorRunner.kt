package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
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

    val bootstrapPercent = AtomicInteger(0)
    @Volatile var ready = false
    @Volatile var failed: String? = null
    @Volatile var started = false

    fun isRunning(): Boolean {
        val lyrebirdOk = lyrebirdProcess == null || lyrebirdProcess?.isAlive == true
        return torProcess?.isAlive == true && lyrebirdOk
    }

    fun isReady(): Boolean = ready && torProcess?.isAlive == true

    fun progress(): Int = bootstrapPercent.get()

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
     * Start the PT (if needed) and the Tor process for this transport.
     * Bridge lines are capped to [MAX_BRIDGE_LINES] to keep torrc manageable.
     */
    fun start(): Result<Unit> {
        stop()
        bootstrapPercent.set(0)
        ready = false
        failed = null
        started = true
        synchronized(recentLog) { recentLog.clear() }

        try {
            val cleanLines = bridgeLines.lines()
                .map { it.trim() }
                .filter { it.isNotBlank() }
                .map { if (it.lowercase().startsWith("bridge ")) it.substring(7).trim() else it }
                .take(MAX_BRIDGE_LINES)

            if (cleanLines.isEmpty()) {
                return Result.failure(RuntimeException("$tag: no bridge lines available"))
            }

            val isVanilla = name == "vanilla"
            val transports = if (isVanilla) mutableListOf<String>() else
                cleanLines.map { it.split("\\s+".toRegex()).firstOrNull()?.lowercase() ?: "" }
                    .filter { it.isNotEmpty() }
                    .distinct()
                    .toMutableList()

            dataDir.mkdirs()
            ptStateDir.mkdirs()
            listOf("state", "lock").forEach { f ->
                val file = File(dataDir, f)
                if (file.exists()) file.delete()
            }

            if (transports.isNotEmpty()) {
                val ptBinary = getObfs4proxyPath()
                    ?: return Result.failure(RuntimeException("$tag: lyrebird (obfs4proxy) binary not found"))
                val result = startLyrebird(ptBinary, transports)
                if (result.isFailure) return result
                val missing = transports.filter { it !in lyrebirdCmethods }
                if (missing.isNotEmpty()) {
                    Log.e(tag, "Lyrebird did not register transports: $missing (only: $lyrebirdCmethods)")
                    stop()
                    return Result.failure(RuntimeException("$tag: PT did not register $missing"))
                }
            }

            extractGeoIpFiles()

            val torrcPath = writeTorrc(cleanLines, isVanilla)
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
                        val match = Regex("Bootstrapped (\\d+)%").find(up)
                        if (match != null) {
                            val pct = match.groupValues[1].toInt()
                            bootstrapPercent.set(pct)
                            Log.i(tag, "Bootstrap: $pct%")
                            if (pct >= 100) {
                                ready = true
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

    private fun writeTorrc(cleanLines: List<String>, isVanilla: Boolean): String {
        val hasSlowTransport = name == "webtunnel"
        val transports = cleanLines.map { it.split("\\s+".toRegex()).firstOrNull()?.lowercase() ?: "" }
            .filter { it.isNotEmpty() }
            .distinct()

        val torrcFile = File(dataDir, "torrc")
        val common = buildString {
            appendLine("SocksPort $listenHost:$torSocksPort")
            appendLine("DataDirectory ${dataDir.absolutePath}")
            appendLine("UseBridges 1")
            val geoipFile = File(dataDir, "geoip")
            val geoip6File = File(dataDir, "geoip6")
            if (geoipFile.exists()) appendLine("GeoIPFile ${geoipFile.absolutePath}")
            if (geoip6File.exists()) appendLine("GeoIPv6File ${geoip6File.absolutePath}")
            appendLine("Log info stdout")
            appendLine("CircuitBuildTimeout ${if (hasSlowTransport) 120 else 60}")
            appendLine("LearnCircuitBuildTimeout 0")
            appendLine("KeepalivePeriod 30")
            appendLine("NumEntryGuards 1")
            appendLine("ClientUseIPv4 1")
            appendLine("ClientUseIPv6 1")
            appendLine("ClientPreferIPv6ORPort auto")
            appendLine("SafeLogging 0")
            appendLine("AvoidDiskWrites 1")
            appendLine("DormantClientTimeout 2419200")
            appendLine("ClientBootstrapConsensusAuthorityDownloadInitialDelay 0")
            appendLine("ConnectionPadding 1")
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

        val exitDirective = buildString {
            val ccs = ExitNodes.currentCodes()
                .map { it.trim().uppercase() }
                .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
                .distinct()
            if (ccs.isNotEmpty()) {
                appendLine("ExitNodes " + ccs.joinToString(",") { "{$it}" })
                appendLine("StrictNodes 1")
            }
        }

        val templateLines = TorrcSettings.templateLines()
        val torrcContent = "$common\n${templateLines.joinToString("\n")}\n${pluginDirectives.toString().trim()}\n${bridgeDirectives.toString().trim()}\n$exitDirective\n"
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

    private fun extractGeoIpFiles() {
        for (name in listOf("geoip", "geoip6")) {
            val destFile = File(dataDir, name)
            if (destFile.exists()) continue
            try {
                context.assets.open(name).use { input ->
                    destFile.outputStream().use { output -> input.copyTo(output) }
                }
                Log.d(tag, "Extracted $name to ${destFile.absolutePath}")
            } catch (e: Exception) {
                Log.w(tag, "Failed to extract $name (may not be bundled): ${e.message}")
            }
        }
    }

    /**
     * Stop the Tor and lyrebird processes for this instance.
     */
    fun stop() {
        torProcess?.let { p ->
            try {
                p.destroy()
                Thread.sleep(500)
                if (p.isAlive) p.destroyForcibly()
            } catch (e: Exception) {
                Log.e(tag, "Error stopping Tor", e)
            }
        }
        torProcess = null

        lyrebirdProcess?.let { p ->
            try {
                try { p.outputStream.close() } catch (_: Exception) {}
                Thread.sleep(500)
                if (p.isAlive) p.destroyForcibly()
            } catch (e: Exception) {
                Log.e(tag, "Error stopping lyrebird", e)
            }
        }
        lyrebirdProcess = null
        lyrebirdCmethods.clear()
        ready = false
    }

    companion object {
        private const val MAX_BRIDGE_LINES = 100
    }
}