package io.deltator

import android.app.Activity
import android.app.Application
import android.os.Bundle
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
 * Adivery ads: one 320x50 banner under the header plus one interstitial that
 * opens when the user disconnects the VPN.
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

    /** The full-screen slot shown on VPN disconnect. */
    const val INTERSTITIAL_PLACEMENT = "a7785856-e7ba-4203-b898-6191e394ddf3"

    /** The slot shown when the user returns to the app after a pause. */
    const val APP_OPEN_PLACEMENT = "b4250c54-1a08-455c-9813-b8ac79c3f062"

    /** Coming back sooner than this does not count as returning to the app. */
    private const val AWAY_THRESHOLD_MILLIS = 5_000L

    /**
     * A full-screen ad just played pushes the app-open ad back for this long,
     * so an interstitial never has an app-open ad stacked right behind it
     * (disconnect, then home, then back is the most common path), and closing
     * a slow full-screen ad does not immediately trigger the next one.
     */
    private const val FULL_SCREEN_SUPPRESS_MILLIS = 30_000L

    @Volatile
    private var lastFullScreenShownAt = 0L

    private fun noteFullScreenShown() {
        lastFullScreenShownAt = System.currentTimeMillis()
    }

    private fun fullScreenSuppressed(): Boolean =
        System.currentTimeMillis() - lastFullScreenShownAt < FULL_SCREEN_SUPPRESS_MILLIS

    /** Set once the network refused the slot; the UI then lets it go. */
    val bannerFailed = mutableStateOf(false)

    /** Set on the first successful load, for layouts that make room for it. */
    val bannerLoaded = mutableStateOf(false)

    /**
     * Bind the app id, preload the interstitial and hang one global listener
     * off the SDK. Called exactly once from the Application's onCreate; the
     * banner itself is loaded by its composable when the slot composes.
     */
    fun init(app: Application) {
        // The SDK's own diagnostics go to Logcat on debug builds only, the
        // same gating the official sample uses.
        Adivery.setLoggingEnabled(BuildConfig.DEBUG)
        Adivery.configure(app, APP_ID)

        Adivery.addGlobalListener(object : AdiveryListener() {
            override fun onInterstitialAdLoaded(placementId: String) {
                Log.i(TAG, "interstitial loaded")
            }

            override fun onInterstitialAdShown(placementId: String) {
                Log.i(TAG, "interstitial shown")
            }

            override fun onInterstitialAdClosed(placementId: String) {
                Log.i(TAG, "interstitial closed")
            }

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

        // One prepare. The SDK refills the slot itself after every show, per
        // its auto-caching model.
        Adivery.prepareInterstitialAd(app, INTERSTITIAL_PLACEMENT)

        // The return-to-app pattern Adivery recommends, watching the
        // activity lifecycle from the process root.
        app.registerActivityLifecycleCallbacks(AppOpenAdManager())
    }

    /**
     * Show the interstitial if one is cached. Called from the disconnect tap:
     * the teardown keeps running underneath, and by the time the ad closes
     * the screen is already back to its disconnected state.
     */
    /**
     * Warm the app open slot. The SDK wants an Activity here, not a Context
     * (unlike the interstitial), so this runs from the main activity's
     * onCreate rather than from [init]; the SDK refills by itself afterwards.
     */
    fun prepareAppOpenAd(activity: Activity) {
        Adivery.prepareAppOpenAd(activity, APP_OPEN_PLACEMENT)
    }

    /**
     * Show the app open ad for a return from [awayMillis] of absence, when
     * both the away threshold and the full-screen suppression window allow
     * it. The suppression stamp is renewed here too: closing a slow app open
     * ad resumes the activity with a large away time, and that resume is the
     * ad closing, not another return.
     */
    fun maybeShowAppOpenAd(activity: Activity, awayMillis: Long) {
        if (awayMillis <= AWAY_THRESHOLD_MILLIS) {
            Log.d(TAG, "app open skipped: back after ${awayMillis}ms away")
            return
        }
        if (fullScreenSuppressed()) {
            Log.d(TAG, "app open skipped: a full screen ad played recently")
            return
        }
        if (Adivery.isLoaded(APP_OPEN_PLACEMENT)) {
            Adivery.showAppOpenAd(activity, APP_OPEN_PLACEMENT)
            noteFullScreenShown()
        } else {
            // The slot is empty. A first load that failed never retries on
            // its own, so every empty-handed return re-arms it for the next
            // one instead of leaving the placement dead for the session.
            Log.i(TAG, "app open not loaded; re-warming the slot")
            Adivery.prepareAppOpenAd(activity, APP_OPEN_PLACEMENT)
        }
    }

    fun showInterstitialIfReady() {
        if (Adivery.isLoaded(INTERSTITIAL_PLACEMENT)) {
            Adivery.showAd(INTERSTITIAL_PLACEMENT)
            noteFullScreenShown()
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

/**
 * The return-to-app pattern Adivery recommends: whenever the user comes back
 * after more than [AdiveryAds.AWAY_THRESHOLD_MILLIS] away, the app open ad
 * shows on the activity being resumed. Registered once from
 * [AdiveryAds.init]; the SDK refills the slot after every show by itself.
 */
private class AppOpenAdManager : Application.ActivityLifecycleCallbacks {

    // Seeded at process start, not zero: the very first resume of a cold
    // launch is the app opening, not the user returning to it, and must not
    // open an ad over the first frame.
    private var lastPauseTime = System.currentTimeMillis()

    override fun onActivityResumed(activity: Activity) {
        AdiveryAds.maybeShowAppOpenAd(
            activity,
            System.currentTimeMillis() - lastPauseTime
        )
    }

    override fun onActivityPaused(activity: Activity) {
        lastPauseTime = System.currentTimeMillis()
    }

    override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
    override fun onActivityStarted(activity: Activity) = Unit
    override fun onActivityStopped(activity: Activity) = Unit
    override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
    override fun onActivityDestroyed(activity: Activity) = Unit
}
