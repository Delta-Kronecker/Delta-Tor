package io.deltator.tunnel

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import io.deltator.util.AppLog

/**
 * The installed apps that split tunnelling can be pointed at.
 *
 * Two rules shape everything here.
 *
 * The list is built from [PackageManager.getInstalledApplications], not from
 * anything the user picked before, because the installed set changes under the
 * app: something gets installed or removed between two connects. Reading it live
 * means the picker never offers a package that is gone.
 *
 * Launchable apps only, and DeltaTor itself is dropped. A service-only package
 * has no icon and no label a user could recognise, so listing it would be noise
 * they cannot act on; and excluding this app is not a policy choice but a
 * requirement, because [android.net.VpnService.Builder.addDisallowedApplication]
 * throws when asked to exclude the package that owns the service. The service
 * already excludes itself for exactly that reason, and a second entry for it here
 * would fail the same way.
 */
object InstalledApps {

    /** One row in the picker. [system] sorts user apps above system ones. */
    data class App(
        val packageName: String,
        val label: String,
        val system: Boolean,
        val icon: android.graphics.drawable.Drawable?
    )

    /**
     * Load the list, sorted the way a user would expect to scan it.
     *
     * Sorted by label rather than package name because the labels are what the
     * user is reading; a package-sorted list on a phone full of apps from two or
     * three vendors puts everything they care about in no particular place.
     *
     * The load is disk- and binder-heavy, so callers should keep it off the main
     * thread. It is not cached: a cache would have to be invalidated on install
     * and uninstall, and this screen is opened a few times a session at most.
     */
    fun load(context: Context, exclude: Set<String>): List<App> {
        val pm = context.packageManager
        val self = context.packageName
        val out = ArrayList<App>(128)

        val installed = try {
            @Suppress("DEPRECATION")
            pm.getInstalledApplications(0)
        } catch (e: Exception) {
            AppLog.e("AppList", "Cannot list installed applications", e)
            return emptyList()
        }

        for (info in installed) {
            val pkg = info.packageName
            if (pkg == self) continue

            val launchable = try {
                pm.getLaunchIntentForPackage(pkg) != null
            } catch (e: Exception) {
                false
            }
            if (!launchable) continue

            val label = try {
                pm.getApplicationLabel(info).toString().ifBlank { pkg }
            } catch (e: Exception) {
                pkg
            }

            val icon = try {
                pm.getApplicationIcon(info)
            } catch (e: Exception) {
                // A launcher icon is decoration. Losing it must not cost the user
                // the ability to route the app.
                null
            }

            out += App(
                packageName = pkg,
                label = label,
                system = (info.flags and ApplicationInfo.FLAG_SYSTEM) != 0,
                icon = icon
            )
        }

        out.sortWith(compareBy({ !it.system }, { it.label.lowercase() }))
        AppLog.i("AppList", "Listed ${out.size} launchable apps (${exclude.size} on the bypass list)")
        return out
    }
}