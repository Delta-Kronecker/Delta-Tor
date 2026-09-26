package io.deltator.tunnel

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * User-selected exit country (optional). Persisted and applied in the generated
 * torrc as `ExitNodes {cc}` + `StrictNodes 1`. A blank code means "any country".
 */
object ExitNodes {
    private const val PREFS = "deltator"
    private const val KEY_CC = "exit_cc"
    private const val KEY_NAME = "exit_name"

    private val _code = MutableStateFlow("")
    private val _name = MutableStateFlow("")
    val code: StateFlow<String> = _code.asStateFlow()
    val name: StateFlow<String> = _name.asStateFlow()

    private var prefs: android.content.SharedPreferences? = null

    fun init(context: Context) {
        if (prefs == null) {
            prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            _code.value = prefs?.getString(KEY_CC, "").orEmpty()
            _name.value = prefs?.getString(KEY_NAME, "").orEmpty()
        }
    }

    fun select(code: String, name: String) {
        _code.value = code
        _name.value = name
        prefs?.edit()?.putString(KEY_CC, code)?.putString(KEY_NAME, name)?.apply()
    }

    fun clear() {
        _code.value = ""
        _name.value = ""
        prefs?.edit()?.remove(KEY_CC)?.remove(KEY_NAME)?.apply()
    }

    /** Synchronous read for torrc generation. */
    fun currentCode(): String = _code.value

    fun currentName(): String = _name.value
}