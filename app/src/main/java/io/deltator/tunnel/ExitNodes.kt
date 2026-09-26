package io.deltator.tunnel

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * User-selected exit countries (optional, any number of them). Persisted and
 * applied in the generated torrc as a single `ExitNodes {us},{nl},...` line plus
 * `StrictNodes 1`. Selecting nothing means "any country".
 */
object ExitNodes {
    private const val PREFS = "deltator"
    private const val KEY_CODES = "exit_ccs"
    private const val KEY_NAMES = "exit_names"

    /** Countries worth having at the top of the picker: plenty of fast relays. */
    private val RECOMMENDED = listOf(
        "US", "NL", "DE", "SE", "FI", "CA", "FR", "GB", "PL", "AT",
        "CH", "CZ", "RO", "IT", "ES", "DK", "NO", "IE", "AU", "JP",
        "SG", "NZ", "BE", "PT", "HU", "EE", "LV", "LT", "SI", "SK"
    )

    private val _codes = MutableStateFlow<List<String>>(emptyList())
    val codes: StateFlow<List<String>> = _codes.asStateFlow()

    private val _names = MutableStateFlow<Map<String, String>>(emptyMap())
    val names: StateFlow<Map<String, String>> = _names.asStateFlow()

    private var prefs: SharedPreferences? = null

    fun init(context: Context) {
        if (prefs != null) return
        val p = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs = p
        val codes = p.getString(KEY_CODES, "").orEmpty()
            .split(',')
            .map { it.trim().uppercase() }
            .filter { it.length == 2 && it.all { c -> c in 'A'..'Z' } }
            .distinct()
        val names = p.getString(KEY_NAMES, "").orEmpty()
            .split(',')
            .map { it.trim() }
            .filter { it.isNotBlank() }
        _codes.value = codes
        _names.value = names.mapIndexedNotNull { i, n -> codes.getOrNull(i)?.let { it to n } }.toMap()
    }

    fun isSelected(code: String): Boolean = _codes.value.contains(code.uppercase())

    /** Add or remove one country; the order of selection is kept. */
    fun toggle(code: String, name: String) {
        val cc = code.trim().uppercase()
        if (cc.length != 2 || !cc.all { it in 'A'..'Z' }) return
        val currentCodes = _codes.value.toMutableList()
        val currentNames = _names.value.toMutableMap()
        if (currentCodes.remove(cc)) {
            currentNames.remove(cc)
        } else {
            currentCodes.add(cc)
            currentNames[cc] = name
        }
        _codes.value = currentCodes
        _names.value = currentNames
        persist(currentCodes, currentNames)
    }

    fun clear() {
        _codes.value = emptyList()
        _names.value = emptyMap()
        prefs?.edit()?.remove(KEY_CODES)?.remove(KEY_NAMES)?.apply()
    }

    private fun persist(codes: List<String>, names: Map<String, String>) {
        prefs?.edit()
            ?.putString(KEY_CODES, codes.joinToString(","))
            ?.putString(KEY_NAMES, codes.joinToString(",") { names[it].orEmpty() })
            ?.apply()
    }

    /** Synchronous read for torrc generation. */
    fun currentCodes(): List<String> = _codes.value

    /**
     * Picker order: the recommended countries first, then everything else
     * alphabetically, so the useful ones are always reachable without scrolling.
     */
    fun order(all: List<ExitCountry>): List<ExitCountry> {
        val byCode = all.associateBy { it.code }
        val head = RECOMMENDED.mapNotNull { byCode[it] }
        val rest = all.filter { it.code !in RECOMMENDED }.sortedBy { it.name.lowercase() }
        return head + rest
    }
}
