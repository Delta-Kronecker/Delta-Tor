package io.deltator

import android.app.Activity
import android.util.Log
import android.view.LayoutInflater
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import ir.tapsell.mediation.Tapsell
import ir.tapsell.mediation.ad.AdStateListener
import ir.tapsell.mediation.ad.request.RequestResultListener
import ir.tapsell.mediation.ad.show.AdShowCompletionState
import ir.tapsell.mediation.ad.views.ntv.NativeAdView
import ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer

/**
 * Tapsell Mediation: one native banner at the top of the app plus one
 * interstitial video that opens halfway through a connect.
 *
 * The SDK initializes itself through its own ContentProvider using the
 * TapsellMediationAppKey manifest placeholder set in app/build.gradle.kts
 * (the publisher app key), so there is no init call here. The zone ids
 * stay in this one object: swap them for the dashboard's next zones
 * without touching any other file.
 */
object TapsellAds {
    private const val TAG = "Tapsell"

    /** The publisher's NativeBanner zone from the Tapsell dashboard. */
    const val NATIVE_ZONE = "6ade1f5f4e86a816eeb924d3"

    /** The publisher's interstitial-video zone from the Tapsell dashboard. */
    const val INTERSTITIAL_ZONE = "6ade55a24e86a816eeb924dd"

    /** Set once the network refused the slot; the UI then lets it go. */
    val nativeFailed = mutableStateOf(false)

    /** Set on the first successful impression, for layouts that make room. */
    val nativeLoaded = mutableStateOf(false)

    /** The response id of the ad currently bound to the banner, for destroy. */
    private var responseId: String? = null

    /** The interstitial response id, set on a successful request. */
    @Volatile
    private var interstitialId: String? = null

    /**
     * Warm the interstitial slot; the SDK refills it itself after every
     * show. Call once from the main activity's onCreate, and again whenever
     * a trigger finds the slot empty, because a first load that failed
     * never retries on its own.
     */
    fun prepareInterstitialAd() {
        Tapsell.requestInterstitialAd(INTERSTITIAL_ZONE, object : RequestResultListener {
            override fun onSuccess(adId: String) {
                Log.i(TAG, "interstitial loaded")
                interstitialId = adId
            }

            override fun onFailure(message: String) {
                Log.w(TAG, "interstitial request failed: $message")
            }
        })
    }

    /**
     * Show the interstitial if one is cached. The connect keeps running
     * underneath, so it is usually live by the time the ad closes. An
     * empty-handed call re-arms the slot for the next trigger.
     */
    fun showInterstitialIfReady(activity: Activity) {
        val id = interstitialId
        if (id == null) {
            Log.i(TAG, "interstitial not loaded; re-warming the slot")
            prepareInterstitialAd()
            return
        }
        interstitialId = null
        Tapsell.showInterstitialAd(id, activity, object : AdStateListener.Interstitial {
            override fun onAdImpression() {
                Log.i(TAG, "interstitial impression")
            }

            override fun onAdClicked() {
                Log.i(TAG, "interstitial clicked")
            }

            override fun onAdFailed(message: String) {
                Log.w(TAG, "interstitial show failed: $message")
            }

            override fun onAdClosed(completionState: AdShowCompletionState) {
                Log.i(TAG, "interstitial closed: ${completionState.name}")
            }
        })
    }

    /** Release the loaded banner ad; called when the banner leaves the composition. */
    fun destroy() {
        responseId?.let {
            Tapsell.destroyNativeAd(it)
            Log.i(TAG, "native banner destroyed")
        }
        responseId = null
    }

    /**
     * One request, then one show into the inflated container. A failed
     * request is terminal for the slot this session -- the composable
     * composes to nothing, no empty rectangle is ever reserved for an ad
     * that will not come.
     */
    fun loadAndShow(activity: Activity, container: NativeAdViewContainer) {
        val title = container.findViewById<android.widget.TextView>(R.id.tapsell_native_ad_title)
        val description =
            container.findViewById<android.widget.TextView>(R.id.tapsell_native_ad_description)
        val logo = container.findViewById<android.widget.ImageView>(R.id.tapsell_native_ad_logo)
        val cta = container.findViewById<android.widget.Button>(R.id.tapsell_native_ad_cta)

        Tapsell.requestNativeAd(NATIVE_ZONE, object : RequestResultListener {
            override fun onSuccess(adId: String) {
                Log.i(TAG, "native banner loaded")
                responseId = adId
                val view = NativeAdView.Builder(container)
                    .withTitle(title)
                    .withDescription(description)
                    .withLogo(logo)
                    .withCtaButton(cta)
                    .build()
                Tapsell.showNativeAd(adId, view, activity, object : AdStateListener.Native {
                    override fun onAdImpression() {
                        Log.i(TAG, "native banner impression")
                        nativeLoaded.value = true
                    }

                    override fun onAdClicked() {
                        Log.i(TAG, "native banner clicked")
                    }

                    override fun onAdFailed(message: String) {
                        // Log only; the collapse is a state write afterwards.
                        Log.w(TAG, "native banner show failed: $message")
                        nativeFailed.value = true
                    }

                    override fun onAdClosed(completionState: AdShowCompletionState) {
                        Log.i(TAG, "native banner closed: ${completionState.name}")
                    }
                })
            }

            override fun onFailure(message: String) {
                Log.w(TAG, "native banner request failed: $message")
                nativeFailed.value = true
            }
        })
    }
}

/**
 * One Tapsell native banner row. The view comes from
 * [R.layout.tapsell_native_banner]: the SDK populates the ids it knows and
 * binds clicks itself. A failed request collapses the slot for the session.
 */
@Composable
fun TapsellNativeBanner(modifier: Modifier = Modifier) {
    val failed by TapsellAds.nativeFailed
    val activity = androidx.compose.ui.platform.LocalContext.current as? Activity
    if (failed || activity == null) return

    // The official sample's cleanup: the loaded ad is released when the
    // banner leaves the composition.
    androidx.compose.runtime.DisposableEffect(Unit) {
        onDispose { TapsellAds.destroy() }
    }

    AndroidView(
        modifier = modifier,
        factory = { context ->
            val container = LayoutInflater.from(context)
                .inflate(R.layout.tapsell_native_banner, null) as NativeAdViewContainer
            TapsellAds.loadAndShow(activity, container)
            container
        }
    )
}
