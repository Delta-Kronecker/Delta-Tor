package io.deltator

import android.app.Activity
import android.app.Application
import android.util.Log
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import com.adivery.sdk.Adivery
import com.adivery.sdk.AdiveryAdListener
import com.adivery.sdk.AdiveryBannerAdView
import com.adivery.sdk.AdiveryListener
import com.adivery.sdk.BannerSize

/**
 * Adivery ads: one 320x50 banner under the header plus one app open full
 * screen that opens halfway through a connect.
 *
 * The app id is bound once in [io.deltator.DeltaTorApp.onCreate] at
 * application start, so the first banner request races the app's own first
 * frame instead of waiting for it. Ids stay in this one object: swap them for
 * the dashboard's next ids without touching any other file.
 */
object AdiveryAds {
    private const val TAG = "Adivery"

    /** The publisher app id this build is bound to. */
    const val APP_ID = "774782cb-3e62-4bd8-a463-6f225e70eabb"

    /** The 320x50 banner slot under the header. */
    const val BANNER_PLACEMENT = "76eec56c-40c4-4c19-8e07-50e4a57f3d11"

    /** The full-screen slot shown at the half mark of a connect. */
    const val APP_OPEN_PLACEMENT = "b4250c54-1a08-455c-9813-b8ac79c3f062"

    /** Set once the network refused the slot; the UI then lets it go. */
    val bannerFailed = mutableStateOf(false)

    /** Set on the first successful load, for layouts that make room for it. */
    val bannerLoaded = mutableStateOf(false)

    /**
     * Bind the app id and hang one global listener off the SDK. Called exactly
     * once from the Application's onCreate; the banner itself is loaded by its
     * composable, and the app open slot is warmed from the main activity
     * because the SDK wants an Activity for it.
     */
    fun init(app: Application) {
        // The SDK's own diagnostics go to Logcat on debug builds only, the
        // same gating the official sample uses.
        Adivery.setLoggingEnabled(BuildConfig.DEBUG)
        Adivery.configure(app, APP_ID)

        Adivery.addGlobalListener(object : AdiveryListener() {
            override fun onAppOpenAdLoaded(placementId: String) {
                Log.i(TAG, "app open loaded")
            }

            override fun onAppOpenAdShown(placementId: String) {
                Log.i(TAG, "app open shown")
            }

            override fun onAppOpenAdClosed(placementId: String) {
                Log.i(TAG, "app open closed")
            }

            override fun log(placementId: String, message: String) {
                // Load failures and other diagnostics: log only, the SDK may
                // report a network that failed while another one still fills
                // the slot, so this is not a verdict.
                Log.d(TAG, "sdk: $message")
            }
        })
    }

    /**
     * Warm the app open slot. The SDK wants an Activity here, not a Context
     * (unlike the banner), so this runs from the main activity's onCreate;
     * the SDK refills by itself after every show.
     */
    fun prepareAppOpenAd(activity: Activity) {
        Adivery.prepareAppOpenAd(activity, APP_OPEN_PLACEMENT)
    }

    /**
     * Show the app open full screen if one is cached. The connect keeps
     * running underneath, so it is usually live by the time the ad closes.
     * An empty slot re-arms itself: a first load that failed never retries
     * on its own.
     */
    fun showAppOpenAdIfReady(activity: Activity) {
        if (Adivery.isLoaded(APP_OPEN_PLACEMENT)) {
            Adivery.showAppOpenAd(activity, APP_OPEN_PLACEMENT)
        } else {
            Log.i(TAG, "app open not loaded; re-warming the slot")
            Adivery.prepareAppOpenAd(activity, APP_OPEN_PLACEMENT)
        }
    }

    fun bannerListener(view: AdiveryBannerAdView) = object : AdiveryAdListener() {
        override fun onAdLoaded() {
            Log.i(TAG, "banner loaded (${view.width}x${view.height})")
            bannerLoaded.value = true
        }

        override fun onError(reason: String) {
            // Log only -- the SDK may fire this more than once, and callers
            // must not run logic inside the callback. The collapse is a plain
            // state write reacting afterwards, not SDK recursion.
            Log.w(TAG, "banner error: $reason")
            bannerFailed.value = true
        }

        override fun onAdClicked() {
            Log.i(TAG, "banner clicked")
        }
    }
}

/**
 * A single Adivery banner, 320x50. One slot, loaded once: the SDK refills it
 * itself after each show, per its auto-caching model. A failed request is
 * terminal for the slot -- the composable composes to nothing, no empty
 * rectangle is ever reserved for an ad that will not come.
 */
@Composable
fun AdiveryBanner(modifier: Modifier = Modifier) {
    val failed by remember { AdiveryAds.bannerFailed }
    if (failed) return

    AndroidView(
        modifier = modifier,
        factory = { context ->
            AdiveryBannerAdView(context).apply {
                setPlacementId(AdiveryAds.BANNER_PLACEMENT)
                setBannerSize(BannerSize.BANNER)
                setBannerAdListener(AdiveryAds.bannerListener(this))
                // One load. Everything after is the SDK's own refresh cycle.
                loadAd()
            }
        }
    )
}
