package io.deltator

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import io.deltator.util.AppLog as Log

/**
 * Receives the result of a PackageInstaller session commit.
 *
 * PackageInstaller does not pop its own dialogs: when it needs the user --
 * for the "install unknown apps" grant, or the plain "do you want to install
 * this app" confirmation -- it sends back STATUS_PENDING_USER_ACTION with a
 * nested Intent that shows the right screen, and this receiver is what has to
 * launch it.
 */
class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(
            PackageInstaller.EXTRA_STATUS,
            PackageInstaller.STATUS_FAILURE
        )
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        when (status) {
            PackageInstaller.STATUS_SUCCESS -> {
                Log.i(TAG, "Install session committed")
                AppState.updateRelease { it.copy(lastError = "") }
            }
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                if (confirm != null) {
                    try {
                        context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    } catch (e: Exception) {
                        Log.w(TAG, "Could not launch install confirmation: ${e.message}")
                        AppState.updateRelease {
                            it.copy(lastError = "Could not open the installer: ${e.message}")
                        }
                    }
                }
            }
            else -> {
                val msg = "Install failed" + (message?.let { ": $it" } ?: ".")
                Log.w(TAG, msg)
                AppState.updateRelease { it.copy(lastError = msg) }
            }
        }
    }

    companion object {
        private const val TAG = "InstallResult"
    }
}

/**
 * Lets the update notification start the install session directly, without
 * needing the app to be open.
 */
class InstallTriggerReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        ReleaseChecker.installLatest(context)
    }
}
