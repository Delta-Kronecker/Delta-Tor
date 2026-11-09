package io.deltator.tunnel

import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.SocketFactory

/**
 * A small, reusable Tor control-port client.
 *
 * Everything here is one plain socket exchange: PROTOCOLINFO to learn what the
 * daemon accepts, AUTHENTICATE with whatever it accepts, then the commands.
 * It exists so that more than one feature can speak to the winner of the race --
 * exit steering did it first (through a private copy of this logic inside
 * [TorRunner]), and "New Identity" on the main screen needs the same channel
 * afterwards. Keeping the exchange in one place means the authentication
 * mistakes live in one place too.
 *
 * Authentication follows the control spec the way Tor itself negotiates it:
 *
 *  - `PROTOCOLINFO` is sent first and the `AUTH METHODS=...` line is parsed.
 *  - `NULL` needs only a bare `AUTHENTICATE`.
 *  - `COOKIE` needs the hex of the cookie file. Tor writes that file into the
 *    DataDirectory as `control_auth_cookie` whenever a ControlPort is opened
 *    without an explicit authentication option (its own default since 0.3.5),
 *    so the caller passes the directory and the file is read here.
 *  - `SAFECOOKIE` is not implemented: torrc is ours and always allows the plain
 *    cookie method, and a live SAFECOOKIE handshake without the client key
 *    material is not worth the machinery in an app that controls the torrc.
 *
 * A launcher that accepts `NULL` skips the cookie entirely, so the class works
 * against both the shipped fork and any build with null authentication.
 */
object TorControl {
    private const val TAG = "TorControl"
    private val ISO = Charsets.ISO_8859_1

    /** One line to hand to Tor: the raw command, and how to treat its failure. */
    class Command(val line: String, val optional: Boolean = false)

    /**
     * Run [commands] against the control port on 127.0.0.1:[port].
     *
     * @param dataDir the Tor DataDirectory of the runner, from which the
     *   authentication cookie is read. Null lets only the `NULL` method work.
     * @return true when every mandatory command was answered `250` and every
     *   optional one was either answered `250` or waived with a warning.
     */
    fun send(
        port: Int,
        commands: List<Command>,
        dataDir: File? = null,
        tag: String = TAG
    ): Boolean {
        if (port <= 0) {
            Log.w(tag, "control port $port is not open; nothing to send")
            return false
        }
        SocketFactory.getDefault().createSocket().use { socket ->
            socket.connect(InetSocketAddress("127.0.0.1", port), 4_000)
            socket.soTimeout = 4_000
            val writer = socket.getOutputStream()
            val reader = BufferedReader(InputStreamReader(socket.getInputStream(), ISO))
            authenticate(writer, reader, tag, dataDir)
            for (command in commands) {
                writeLine(writer, command.line)
                val reply = readReply(reader)
                val ok = reply.isNotEmpty() && reply.last().startsWith("250")
                if (!ok) {
                    if (command.optional) {
                        Log.w(tag, "${command.line} -> ${reply.joinToString(" / ")} (ignored)")
                        continue
                    }
                    throw RuntimeException("${command.line} -> ${reply.joinToString(" / ")}")
                }
                Log.i(tag, "${command.line} -> ${reply.last()}")
            }
            return true
        }
    }

    /**
     * Learn the accepted methods and pass the first round we can satisfy.
     * Mandatory: a failed authentication throws and the caller reports it.
     */
    private fun authenticate(
        writer: java.io.OutputStream,
        reader: BufferedReader,
        tag: String,
        dataDir: File?
    ) {
        writeLine(writer, "PROTOCOLINFO 1")
        val info = readReply(reader)
        if (info.isEmpty() || !info.last().startsWith("250")) {
            throw RuntimeException("PROTOCOLINFO -> ${info.joinToString(" / ")}")
        }
        val methodsLine = info.firstOrNull { it.startsWith("250-AUTH") } ?: ""
        val methods = Regex("METHODS=([A-Z,]+)").find(methodsLine)
            ?.groupValues?.get(1)?.split(',')?.map { it.trim() } ?: emptyList()
        when {
            methods.any { it.equals("NULL", ignoreCase = true) } -> {
                writeLine(writer, "AUTHENTICATE")
                val reply = readReply(reader)
                if (!reply.lastOrNull().orEmpty().startsWith("250")) {
                    throw RuntimeException("AUTHENTICATE(NULL) -> ${reply.joinToString(" / ")}")
                }
            }
            methods.any { it.equals("COOKIE", ignoreCase = true) } -> {
                val hex = cookieHexOf(dataDir)
                    ?: error("no control_auth_cookie found in ${dataDir?.path}")
                writeLine(writer, "AUTHENTICATE $hex")
                val reply = readReply(reader)
                if (!reply.lastOrNull().orEmpty().startsWith("250")) {
                    throw RuntimeException("AUTHENTICATE(COOKIE) -> ${reply.joinToString(" / ")}")
                }
            }
            else -> {
                // No method this client can answer. Try the bare handshake and
                // let the reply speak: a daemon that accepts nothing says 514,
                // and that is a configuration problem worth its own message.
                Log.w(tag, "control auth methods were [$methods]; attempting a bare AUTHENTICATE")
                writeLine(writer, "AUTHENTICATE")
                val reply = readReply(reader)
                if (!reply.lastOrNull().orEmpty().startsWith("250")) {
                    throw RuntimeException("AUTHENTICATE -> ${reply.joinToString(" / ")}")
                }
            }
        }
    }

    /**
     * Hex of Tor's `control_auth_cookie` inside [dir], or null when the file
     * has not been written yet (the fallback when the daemon answers NULL and
     * needs no cookie at all).
     */
    fun cookieHexOf(dir: File?): String? {
        if (dir == null) return null
        val file = File(dir, "control_auth_cookie")
        if (!file.exists()) return null
        return runCatching {
            file.readBytes().joinToString("") { "%02x".format(it) }
        }.getOrNull()
    }

    /** `SIGNAL NEWNYM` on a live session: the next streams leave a fresh circuit. */
    fun signalNewIdentity(port: Int, dataDir: File?, tag: String = TAG): Boolean =
        send(port, listOf(Command("SIGNAL NEWNYM", optional = true)), dataDir, tag)

    /**
     * Steer a live Tor into [codes], the same exchange the bootstrap applies:
     * the rules, `StrictNodes 0` so the list can never block a circuit, and a
     * couple of settings whose long-lived defaults fight a mid-session switch
     * (short circuit dirtiness, Conflux paused); then a NEWNYM so the current
     * traffic actually leaves through the new list. Returns true when Tor
     * confirmed the main rules.
     */
    fun steerExit(
        port: Int,
        dataDir: File?,
        codes: List<String>,
        tag: String = TAG
    ): Boolean {
        if (codes.isEmpty()) return clearExit(port, dataDir, tag)
        val exitValue = codes.joinToString(",") { "{$it}" }
        return send(
            port,
            listOf(
                Command("SETCONF ExitNodes=\"$exitValue\"", optional = false),
                Command("SETCONF StrictNodes=0", optional = false),
                Command("SETCONF MaxCircuitDirtiness=600", optional = true),
                Command("SETCONF ConfluxEnabled=0", optional = true),
                Command("SETCONF CircuitBuildTimeout=180", optional = true),
                Command("SETCONF LearnCircuitBuildTimeout=1", optional = true),
                Command("SIGNAL NEWNYM", optional = true),
                Command("GETCONF ExitNodes", optional = false),
                Command("GETCONF StrictNodes", optional = false)
            ),
            dataDir,
            tag
        )
    }

    /** Take any live exit steering back off: no list, no strictness, fresh circuit. */
    fun clearExit(port: Int, dataDir: File?, tag: String = TAG): Boolean =
        send(
            port,
            listOf(
                Command("SETCONF ExitNodes", optional = false),
                Command("SETCONF StrictNodes=0", optional = false),
                Command("SIGNAL NEWNYM", optional = true)
            ),
            dataDir,
            tag
        )

    private fun writeLine(writer: java.io.OutputStream, line: String) {
        writer.write((line + "\r\n").toByteArray(ISO))
        writer.flush()
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
            while (true) {
                val data = reader.readLine() ?: break
                if (data == ".") break
                out.add(data)
            }
            reader.readLine()?.let { out.add(it) }
        } else {
            while (line.startsWith("250-")) {
                line = reader.readLine() ?: break
                out.add(line)
            }
        }
        return out
    }
}
