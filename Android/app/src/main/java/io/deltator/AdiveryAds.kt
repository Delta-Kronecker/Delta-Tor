package io.deltator

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
    }

    /**
     * Show the interstitial if one is cached. Called from the disconnect tap:
     * the teardown keeps running underneath, and by the time the ad closes
     * the screen is already back to its disconnected state.
     */
    fun showInterstitialIfReady() {
        if (Adivery.isLoaded(INTERSTITIAL_PLACEMENT)) {
            Adivery.showAd(INTERSTITIAL_PLACEMENT)
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
