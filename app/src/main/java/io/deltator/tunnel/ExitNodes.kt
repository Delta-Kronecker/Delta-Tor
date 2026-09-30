package io.deltator.tunnel

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import java.util.concurrent.atomic.AtomicBoolean

/**
 * User-selected exit countries (optional, any number of them). Persisted and
 * applied in the generated torrc as a single `ExitNodes {us},{nl},...` line plus
 * `StrictNodes 0`, so the list steers the exit country but can never make a
 * circuit impossible to build. Selecting nothing means "any country".
 */
object ExitNodes {
    private const val PREFS = "deltator"
    private const val KEY_CODES = "exit_ccs"
    private const val KEY_NAMES = "exit_names"

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
     * Picker order: the countries that actually have exits right now, most exit
     * bandwidth first, then everything else alphabetically.
     *
     * The split is not a curated list, it is the relay database's own answer, so
     * it cannot go stale and it cannot disagree with what Tor will actually
     * build. Before the first fetch lands, or with no network, everything is
     * simply alphabetical.
     */
    private fun order(all: List<ExitCountry>, capacity: Map<String, ExitCapacity>): List<ExitCountry> {
        if (capacity.isEmpty()) return all
        val byCode = all.associateBy { it.code }
        val head = capacity.entries
            .sortedByDescending { it.value.weight }
            .mapNotNull { byCode[it.key] }
        val rest = all.filter { it.code !in capacity }.sortedBy { it.name.lowercase() }
        return head + rest
    }

    private val _all = MutableStateFlow<List<ExitCountry>>(emptyList())
    private val directoryLoaded = AtomicBoolean(false)

    /**
     * The ordered picker list. Re-emits when the country table arrives and again
     * when the exit capacity does, so the list refines itself in place instead
     * of waiting on the network before the drawer can be opened.
     */
    val directory: StateFlow<List<ExitCountry>> =
        combine(_all, ExitCapacityIndex.byCountry) { all, capacity -> order(all, capacity) }
            .stateIn(
                scope = CoroutineScope(Dispatchers.Default),
                started = SharingStarted.Eagerly,
                initialValue = emptyList()
            )

    fun loadDirectory(context: Context) {
        if (!directoryLoaded.compareAndSet(false, true)) return
        val app = context.applicationContext
        ExitCapacityIndex.load(app)
        Thread({
            _all.value = runCatching { BridgeCountries.topSync(app) }.getOrDefault(emptyList())
        }, "deltator-countries").apply { isDaemon = true }.start()
    }
}
