package io.deltator.tunnel

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File

enum class TorrcOptionType { STRING, INT, BOOL }

data class TorrcOption(
    val key: String,
    val label: String,
    val hint: String,
    val type: TorrcOptionType,
    val placeholder: String = ""
)

/**
 * Curated "basic torrc" settings surfaced in the app's Settings section.
 *
 * Every option is a torrc directive. Non-blank values are appended to the
 * generated torrc on the next connect (later lines win, so they override the
 * hardcoded defaults in [TorRunner]).
 */
object TorrcSettings {

    val options: List<TorrcOption> = listOf(
        TorrcOption(
            key = "CircuitBuildTimeout",
            label = "Circuit Build Timeout",
            hint = "Seconds before a circuit attempt is considered failed",
            type = TorrcOptionType.INT,
            placeholder = "60"
        ),
        TorrcOption(
            key = "NumEntryGuards",
            label = "Entry Guards",
            hint = "Number of entry guards Tor keeps in parallel",
            type = TorrcOptionType.INT,
            placeholder = "1"
        ),
        TorrcOption(
            key = "KeepalivePeriod",
            label = "Keepalive Period",
            hint = "Seconds between keepalive cells to idle relays",
            type = TorrcOptionType.INT,
            placeholder = "30"
        ),
        TorrcOption(
            key = "ClientUseIPv4",
            label = "Use IPv4",
            hint = "Allow connections over IPv4 relays",
            type = TorrcOptionType.BOOL
        ),
        TorrcOption(
            key = "ClientUseIPv6",
            label = "Use IPv6",
            hint = "Allow connections over IPv6 relays",
            type = TorrcOptionType.BOOL
        ),
        TorrcOption(
            key = "StrictNodes",
            label = "Strict Exit Policy",
            hint = "Never use relays outside ExitNodes / ExcludeExitNodes",
            type = TorrcOptionType.BOOL
        ),
        TorrcOption(
            key = "ExitNodes",
            label = "Exit Nodes",
            hint = "Restrict exits by country code, e.g. {us} or {us},{de}",
            type = TorrcOptionType.STRING,
            placeholder = "{us}"
        ),
        TorrcOption(
            key = "ExcludeExitNodes",
            label = "Exclude Exits",
            hint = "Country codes to avoid for exits, e.g. {cn},{ru}",
            type = TorrcOptionType.STRING,
            placeholder = "{cn}"
        ),
        TorrcOption(
            key = "Log",
            label = "Tor Log Level",
            hint = "notice | info | debug — written to stdout and the in-app log",
            type = TorrcOptionType.STRING,
            placeholder = "info"
        )
    )

    private const val PREFS = "deltator"
    private lateinit var prefs: SharedPreferences

    private val _values = MutableStateFlow<Map<String, String>>(emptyMap())
    val values: StateFlow<Map<String, String>> = _values.asStateFlow()

    fun init(context: Context) {
        prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val m = HashMap<String, String>()
        for (o in options) {
            prefs.getString("torrc.${o.key}", null)?.let { m[o.key] = it }
        }
        _values.value = m
    }

    fun valueOf(key: String): String = _values.value[key] ?: ""

    fun set(key: String, value: String) {
        val v = value.trim()
        val m = _values.value.toMutableMap()
        if (v.isEmpty()) m.remove(key) else m[key] = v
        _values.value = m
        val editor = prefs.edit()
        if (v.isEmpty()) editor.remove("torrc.$key") else editor.putString("torrc.$key", v)
        editor.apply()
    }

    fun reset(key: String) = set(key, "")

    fun resetAll() {
        options.forEach { reset(it.key) }
    }

    /**
     * torrc directives from enabled customisations, appended last so they win
     * over the generated defaults. Empty options are skipped.
     */
    fun overrideLines(): List<String> {
        val out = ArrayList<String>()
        for (o in options) {
            val v = _values.value[o.key] ?: continue
            if (v.isEmpty()) continue
            out += if (o.key == "Log") {
                "Log $v stdout"
            } else {
                "${o.key} $v"
            }
        }
        return out
    }

    // --- manual torrc file ---

    fun customTorrc(): String = prefs.getString("torrc_custom", "") ?: ""

    fun setCustomTorrc(text: String) {
        prefs.edit().putString("torrc_custom", text.trim()).apply()
    }

    /** Non-blank, non-comment lines from the user-edited torrc block. */
    fun customLines(): List<String> = customTorrc().lines()
        .map { it.trim() }
        .filter { it.isNotBlank() && !it.startsWith("#") }

    /** Content of the last torrc actually generated, for read-only preview. */
    fun readLastGenerated(context: Context): String {
        val f = File(context.filesDir, "tor_last.torrc")
        return try {
            if (f.exists()) f.readText() else ""
        } catch (e: Exception) {
            ""
        }
    }
}