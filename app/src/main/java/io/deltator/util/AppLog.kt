package io.deltator.util

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

data class LogEntry(
    val id: Long,
    val raw: String,
    val level: Char,
    /** Connection session this line belongs to (0 = before the first connection). */
    val session: Int = 0,
    /** Transport that produced the line: vanilla / obfs4 / webtunnel, null = shared. */
    val transport: String? = null
)

/**
 * One connect attempt. Every log line is stamped with the session it belongs to
 * so the log screen can show each connection separately, with its own header and
 * outcome.
 */
data class LogSession(
    val id: Int,
    val label: String,
    val startedAtMillis: Long,
    /** Set when the attempt finishes, e.g. "connected via obfs4" or the failure reason. */
    val outcome: String? = null
)

/**
 * In-memory log buffer that wraps [android.util.Log].
 *
 * Every call forwards to the real Android logger AND appends to a ring buffer
 * that the debug-log UI can observe without spawning a `logcat` process
 * (which triggers Google Play Protect).
 *
 * Usage: replace `import android.util.Log` with
 *        `import app.slipnet.util.AppLog as Log`
 * — no other code changes needed.
 */
object AppLog {
    private const val MAX_LINES = 1500
    private val nextId = AtomicLong(0)
    private val buffer = ArrayDeque<LogEntry>()

    /** Live entry count per transport ("" = shared lines), used for fair trimming. */
    private val perTransport = HashMap<String, Int>()

    /** When true, sensitive config details are redacted from the in-app log buffer. */
    @Volatile var redactSensitive = false

    // --- connect sessions -----------------------------------------------------
    private val sessionLock = Any()
    private val sessionIds = AtomicLong(0)
    private val _sessions = MutableStateFlow<List<LogSession>>(emptyList())
    val sessions: StateFlow<List<LogSession>> = _sessions.asStateFlow()
    private val currentSession = AtomicLong(0)

    /**
     * Begin a new connection session. Subsequent lines are tagged with its id
     * until the next [beginSession]. The returned id is stamped on every entry.
     */
    fun beginSession(label: String): Int = synchronized(sessionLock) {
        val id = sessionIds.incrementAndGet().toInt()
        currentSession.set(id.toLong())
        _sessions.value = (_sessions.value + LogSession(id, label, System.currentTimeMillis())).takeLast(12)
        append('=', "DeltaTor", "=== connection #$id \u00b7 $label ===")
        id
    }

    /** Mark the current session as finished and record the outcome on its header. */
    fun endSession(outcome: String) = synchronized(sessionLock) {
        val id = currentSession.get().toInt()
        if (id == 0) return@synchronized
        _sessions.value = _sessions.value.map {
            if (it.id == id) it.copy(outcome = outcome) else it
        }
        append('=', "DeltaTor", "=== connection #$id \u00b7 $outcome ===")
    }

    private fun sessionOf(): Int = currentSession.get().toInt()

    // Lazy snapshot — only rebuilt when the debug sheet is open (observerCount > 0).
    private val _lines = MutableStateFlow<List<LogEntry>>(emptyList())
    val lines: StateFlow<List<LogEntry>> = _lines.asStateFlow()

    // Track whether anyone is observing so we skip work when not needed.
    @Volatile var observerCount = 0
        private set

    // Dirty flag: set by append(), cleared by flush().
    // Avoids creating an ArrayList copy on every single log call — instead
    // the UI polls via flushIfDirty() on each collection (every frame).
    private val dirty = AtomicBoolean(false)

    private val dateFormat = object : ThreadLocal<SimpleDateFormat>() {
        override fun initialValue() = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)
    }

    private fun append(level: Char, tag: String, msg: String) {
        appendFor(level, tag, msg, transportOf(tag))
    }

    /** Transports raced in parallel; the log screen lets the user pick one. */
    val TRANSPORTS = listOf("vanilla", "obfs4", "webtunnel", "memory")

    private val runnerTag = Regex("""TorRunner\[(\w+)]""")

    private fun transportOf(tag: String): String? =
        runnerTag.find(tag)?.groupValues?.get(1)?.takeIf { it in TRANSPORTS }

    private fun appendFor(level: Char, tag: String, msg: String, transport: String?) {
        val id = nextId.getAndIncrement()
        val entry = if (observerCount > 0) {
            val ts = dateFormat.get()!!.format(Date())
            LogEntry(id, "$ts $level/$tag: $msg", level, sessionOf(), transport)
        } else {
            // Lightweight entry — no timestamp formatting when nobody is watching
            LogEntry(id, "$level/$tag: $msg", level, sessionOf(), transport)
        }
        synchronized(buffer) {
            buffer.addLast(entry)
            perTransport[transport ?: ""] = (perTransport[transport ?: ""] ?: 0) + 1
            trimLocked()
        }
        if (observerCount > 0) {
            dirty.set(true)
        }
    }

    /**
     * Keep the buffer bounded without letting one noisy transport (vanilla emits
     * thousands of Tor lines) evict the other two: always drop the oldest line
     * of whichever transport currently holds the most of them.
     */
    private fun trimLocked() {
        while (buffer.size > MAX_LINES) {
            var victim = ""
            var worst = 0
            for ((t, n) in perTransport) {
                if (n > worst) {
                    worst = n
                    victim = t
                }
            }
            val idx = buffer.indexOfFirst { (it.transport ?: "") == victim }
            if (idx < 0) {
                // Should not happen; fall back to plain FIFO so we never spin.
                val dropped = buffer.removeFirst()
                val key = dropped.transport ?: ""
                perTransport[key] = (perTransport[key] ?: 1) - 1
                continue
            }
            buffer.removeAt(idx)
            perTransport[victim] = (perTransport[victim] ?: 1) - 1
        }
    }

    /**
     * Copy the buffer to the StateFlow if anything changed since the last flush.
     * Called by the debug sheet on a periodic timer (~100ms) so we batch many
     * rapid log calls into a single ArrayList copy + recomposition.
     */
    fun flushIfDirty() {
        if (dirty.compareAndSet(true, false)) {
            synchronized(buffer) {
                _lines.value = ArrayList(buffer)
            }
        }
    }

    /** Call from debug sheet onStart/onStop to enable/disable snapshots. */
    fun addObserver() {
        observerCount++
        // Immediately snapshot current buffer for new observer
        synchronized(buffer) {
            _lines.value = ArrayList(buffer)
        }
    }

    fun removeObserver() {
        observerCount = (observerCount - 1).coerceAtLeast(0)
    }

    /** Tags whose messages contain sensitive config details (hosts, ports, credentials). */
    private val SENSITIVE_TAGS = setOf(
        "HevSocks5Tunnel",
        "SlipstreamSocksBridge",
        "DnsttSocksBridge",
        "SshTunnelBridge",
        "SlipNetVpnService",
        "KotlinTunnelManager",
        "NaiveSocksBridge",
        "TorSocksBridge",
        "SlipstreamBridge",
        "DnsttBridge",
        "NaiveBridge",
        "VpnRepositoryImpl",
        "VaydnsBridge",
        "DnsResolverProber",
        "DohBridge",
        "HttpProxyServer",
        "ProxyHttpConnect",
        "ProxyWebSocket",
        "TlsSocketFactory",
        "NaiveSocksProxy",
        "PayloadSocketFactory",
        "DomainRouter",
        "DnsDoHProxy"
    )

    /** Tag prefixes for dynamic tags (e.g. SshTunnel[default], Socks5Proxy[0]). */
    private val SENSITIVE_TAG_PREFIXES = arrayOf("SshTunnel[", "Socks5Proxy[")

    /**
     * Check if this log line should be redacted from the in-app buffer.
     * For locked profiles, all messages from sensitive tags are suppressed
     * (still forwarded to Android logcat which requires ADB access).
     */
    private fun shouldRedact(tag: String): Boolean {
        if (!redactSensitive) return false
        if (tag in SENSITIVE_TAGS) return true
        return SENSITIVE_TAG_PREFIXES.any { tag.startsWith(it) }
    }

    fun v(tag: String, msg: String): Int {
        if (!shouldRedact(tag)) append('V', tag, msg)
        return android.util.Log.v(tag, msg)
    }

    fun d(tag: String, msg: String): Int {
        if (!shouldRedact(tag)) append('D', tag, msg)
        return android.util.Log.d(tag, msg)
    }

    fun i(tag: String, msg: String): Int {
        if (!shouldRedact(tag)) append('I', tag, msg)
        return android.util.Log.i(tag, msg)
    }

    fun w(tag: String, msg: String): Int {
        if (!shouldRedact(tag)) append('W', tag, msg)
        return android.util.Log.w(tag, msg)
    }

    @JvmStatic
    fun w(tag: String, msg: String, tr: Throwable?): Int {
        if (!shouldRedact(tag)) append('W', tag, if (tr != null) "$msg\n${tr.stackTraceToString()}" else msg)
        return android.util.Log.w(tag, msg, tr)
    }

    fun e(tag: String, msg: String): Int {
        if (!shouldRedact(tag)) append('E', tag, msg)
        return android.util.Log.e(tag, msg)
    }

    @JvmStatic
    fun e(tag: String, msg: String, tr: Throwable?): Int {
        if (!shouldRedact(tag)) append('E', tag, if (tr != null) "$msg\n${tr.stackTraceToString()}" else msg)
        return android.util.Log.e(tag, msg, tr)
    }

    fun clear() {
        synchronized(buffer) {
            buffer.clear()
            perTransport.clear()
            _lines.value = emptyList()
        }
    }

    /**
     * Log an event tied to a specific connect session, used by the transport
     * runner threads so their lines land in the right section even though they
     * are not the connection owner.
     */
    fun session(sessionId: Int, level: Char, tag: String, msg: String) {
        synchronized(sessionLock) {
            val prev = currentSession.get()
            currentSession.set(sessionId.toLong())
            try {
                append(level, tag, msg)
            } finally {
                currentSession.set(prev)
            }
        }
    }

    /** Same as [session] but also attributes the line to one transport. */
    fun transport(
        sessionId: Int,
        transport: String,
        level: Char,
        tag: String,
        msg: String
    ) {
        synchronized(sessionLock) {
            val prev = currentSession.get()
            currentSession.set(sessionId.toLong())
            try {
                appendFor(level, tag, msg, transport)
            } finally {
                currentSession.set(prev)
            }
        }
    }
}
