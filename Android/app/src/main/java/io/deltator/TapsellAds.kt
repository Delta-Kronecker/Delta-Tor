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
 * Tapsell Mediation, native banner only for now.
 *
 * The SDK initializes itself through its own ContentProvider using the
 * TapsellMediationAppKey manifest placeholder set in app/build.gradle.kts,
 * so there is no init call here. The ids stay in this one object: the app
 * key swap lives in the gradle placeholder, the zone id lives below, and
 * both are the Tapsell test values until the dashboard's real ones are in.
 */
object TapsellAds {
    private const val TAG = "Tapsell"

    /** The Tapsell test app key; the gradle placeholder carries the same value. */
    const val TEST_APP_KEY = "76798342-99a7-4a5f-bf5a-60a088d5dcfb"

    /**
     * The publisher's real NativeBanner zone (their dashboard), parked here
     * until the real app key lands in the gradle placeholder -- a real zone
     * under the test app key fills nothing.
     */
    const val REAL_NATIVE_ZONE = "6ade1f5f4e86a816eeb924d3"

    /** The Tapsell Legacy test zone for native ads, shown while testing. */
    const val NATIVE_ZONE = "b1b92bca-8a54-4b82-9f53-90ea2b1a912c"

    /** Set once the network refused the slot; the UI then lets it go. */
    val nativeFailed = mutableStateOf(false)

    /** Set on the first successful impression, for layouts that make room. */
    val nativeLoaded = mutableStateOf(false)

    /** The response id of the ad currently bound to the banner, for destroy. */
    private var responseId: String? = null

    /** Release the loaded ad; called when the banner leaves the composition. */
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
