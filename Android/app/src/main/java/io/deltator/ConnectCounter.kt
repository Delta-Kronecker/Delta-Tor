package io.deltator

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * Counts connections by downloading a one byte asset from a dedicated release
 * once per connect the user asks for.
 *
 * GitHub increments the download counter of a release asset on every request,
 * so the asset's download count is the number of connections. The asset is
 * read into memory and discarded immediately -- never written to disk -- so
 * there is no file to clean up and no storage is consumed.
 *
 * Fire-and-forget: called from the connect handler without blocking it, so a
 * slow or failed request never delays the actual connection. Retries live in
 * [StatBeacon] and happen only when the previous request really failed, which
 * is what keeps one connection from counting twice.
 */
object ConnectCounter {
    private const val TAG = "ConnectCounter"
    private const val ASSET_URL =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
            "connected/connected"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    fun countConnect() {
        scope.launch { StatBeacon.hit(TAG, ASSET_URL, "DeltaTor-Android") }
    }
}