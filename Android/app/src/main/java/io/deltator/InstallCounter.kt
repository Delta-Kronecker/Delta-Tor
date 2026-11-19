package io.deltator

import android.content.Context
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Counts installs by downloading a one byte asset from a dedicated release.
 *
 * GitHub increments the download counter of a release asset on every request,
 * so a single successful fetch per installation is enough to make that number
 * the install count. The asset is never stored and never parsed, only the
 * counter matters.
 *
 * The done flag is written only after a request actually reached the asset, so
 * the number is exactly one per installation: every launch before that
 * success retries (with backoff, see [StatBeacon]), every launch after it does
 * nothing at all.
 */
object InstallCounter {
    private const val TAG = "InstallCounter"
    private const val PREFS = "deltator_install"
    private const val KEY_DONE = "counted_v1"
    private const val ASSET_URL =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
            "ForInstallationStatistics/DeltaTorAndroid213"

    suspend fun countInstall(context: Context) = withContext(Dispatchers.IO) {
        val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (prefs.getBoolean(KEY_DONE, false)) return@withContext
        if (StatBeacon.hit(TAG, ASSET_URL, "DeltaTor-Android")) {
            prefs.edit().putBoolean(KEY_DONE, true).apply()
            Log.i(TAG, "Install counted")
        }
    }
}