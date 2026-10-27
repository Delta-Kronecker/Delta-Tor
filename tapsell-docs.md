# مستندات تپسل (Tapsell Mediation — Android)

منبع: https://developer.tapsell.ir/fa/docs/sdk/ — دریافت‌شده در 2026-10-25

# مقدمه

URL: https://developer.tapsell.ir/fa/docs/sdk/

---

# Introduction

> Source: https://developer.tapsell.ir/docs/sdk

Tapsell provides you a SDK so that you can easily use Tapsell with any type of application you have, easily implement the
Tapsell SDK in your application and earn money from ads.

Tapsell's publisher relations team is also with you from the very
beginning of the Tapsell SDK implementation to help you achieve the maximum advertising capacity without harming the user
experience and get the most profit from ads by optimally implementing the SDK.

## Mediation SDK {#mediation}

:::success â

Tapsell Mediation, is a Mediated solution helping businesses increase their mobile appsâ revenue with the inclusion of
other supported Programmatic & Mediated Ad Solutions.

Integrating the Tapsell SDK into your app is the first step toward displaying ads and earning revenue. Once youâve
integrated the SDK, you can choose an ad format (such as native or rewarded video) and follow the steps to implement it.

- [Android Studio docs](./platforms/android)
- [Unity docs](./platforms/unity)
- [Flutter docs](./platforms/flutter)
- [React Native docs](./platforms/reactnative)

:::

## Tapsell Plus SDK

:::warning This library will not be supported in the future

If you have been using Tapsell Plus for in-app advertising so far, it is better to use the
[Tapsell Mediation SDK](#mediation) as a replacement.
The Tapsell Mediation SDK, in addition to having all the features of Tapsell Plus, provides smarter algorithms,
more diverse ads, and better performance.

Released versions of Tapsell Plus will continue to work and receive ads if they have already been implemented; however,
they will not be updated and bugs will not be supported.

- [Android docs](/docs/sdk-legacy-tapsell-plus/android)
- [Unity docs](/docs/sdk-legacy-tapsell-plus/unity)
- [Flutter docs](/docs/sdk-legacy-tapsell-plus/flutter)
- [React Native docs](/docs/sdk-legacy-tapsell-plus/reactnative)
- [B4A docs](/docs/sdk-legacy-tapsell-plus/b4a)

:::

## Tapsell SDK

:::danger This library is no longer supported

If you have been using Tapsell for in-app advertising so far, it is better to use the
[Tapsell Mediation SDK](#mediation) as a replacement.

The Tapsell Mediation SDK, in addition to having all the features of Tapsell, provides smarter algorithms,
more diverse ads, and better performance.

It also provides the opportunity to earn foreign-currency revenue by showing ads from external ad networks
(such as Google AdMob).

- [Android docs](/docs/sdk-legacy/android)
- [Unity docs](/docs/sdk-legacy/unity)
- [React Native docs](/docs/sdk-legacy/reactnative)
- [B4A docs](/docs/sdk-legacy/b4a)

:::


# اندروید استودیو — شروع سریع

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/quick-start/

---

# Quick Start

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/quick-start

Integrating the Tapsell Mobile Ads SDK into an app is the first step to ad impressions and revenue. Once the SDK is
integrated, you can choose an ad format to be used for your app monetization, such as banner ads or rewarded ads, and
perform its setup.

## Prerequisites

To prepare your app, follow the steps described in the next sections.

### App requirements

Ensure that you're using the following config in your project settings:

:::tip Project configs

- Use Android Studio **2023** or higher.
- Use Android Gradle Plugin `7.4.2` or higher.
- Check that your app's `build.gradle` file uses the following values:
    - `MinSdkVersion` **21** or higher.
    - `CompileSdkVersion` **35** or higher.
      :::

### Set up the app in your Tapsell Advertising Network account

Here's how to register your app in the Tapsell Advertising Network:

1. [Log in or register](https://app.tapsell.ir) your account in the Tapsell Advertising Network.
2. Register your app in the Tapsell Advertising Network and choose **Android** as the development platform.
3. Add the ad networks you need in the Tapsell Advertising Network.

## Set up your app

1. Add support for the `Google` and `MavenCentral` repositories to the project configuration.

Depending on the project build settings, these repositories can be added either to the project's `build.gradle` file or to
the repositories section of the `settings.gradle` file as shown below:

<Tabs
    groupId="gradleType" queryString lazy defaultValue="settings.gradle">
        ```groovy
        // Project-level settings.gradle file
        dependencyResolutionManagement {
            repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
            repositories {
                maven { url = uri("https://maven.tapsell.ir") }
                google()
                mavenCentral()
            }
        }
        ```
        ```groovy
        // Project-level build.gradle file
        allprojects {
            repositories {
                maven { url = uri("https://maven.tapsell.ir") }
                google()
                mavenCentral()
            }
        }
        ```

2. Add a dependency on the Tapsell Mobile Ads SDK to a Gradle file at your app's `build.gradle` file:

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

<Tabs
    groupId="gradleType" queryString lazy defaultValue="build.gradle.kts">
        ```kotlin
        dependencies {
            implementation("ir.tapsell:tapsell:$tapsellVersion")
            implementation("ir.tapsell.mediation.adapter:legacy:$tapsellVersion")
        }
        ```
        ```groovy
        dependencies {
            implementation "ir.tapsell:tapsell:$tapsellVersion"
            implementation "ir.tapsell.mediation.adapter:legacy:$tapsellVersion"
        }
        ```

:::note Proguard/R8 rules:
If you are using R8 for shrinking and obfuscation, required rules are included automatically.
:::

3. Add a manifest placeholder inside your moduleâs app-level `build.gradle` providing your Tapsell App ID collected from your dashboard:

<Tabs
    groupId="gradleType" queryString lazy defaultValue="build.gradle.kts">
        ```kotlin
        android {
            defaultConfig {
                addManifestPlaceholders(
                    mapOf("TapsellMediationAppKey" to "YOUR_APP_ID")
                )
            }
        }
        ```
        ```groovy
        android {
            defaultConfig {
                manifestPlaceholders = [
                    TapsellMediationAppKey: "YOUR_APP_ID",
                ]
            }
        }
        ```

4. Set up permission to use **Google Advertising ID**. The `GAID` is a unique identifier provided by **Google Play services**
for displaying ads to users who opt in to personalized ads. Users can opt out of ad personalization or reset their ID in
the settings. In this case, advertising networks won't be able to use the ID to select relevant ads for the user.
[Read More](https://support.google.com/googleplay/android-developer/answer/6048248?hl=en)

Add **`com.google.android.gms.permission.AD_ID`** to the `AndroidManifest.xml` file as follows:

```xml title="src/main/AndroidManifest.xml"
<manifest>
    <application>
    <!-- For apps targeting Android 13 or higher -->
        <uses-permission android:name="com.google.android.gms.permission.AD_ID"/>
    </application>
</manifest>
```

:::warning
Lack of permission and access to the `GAID` may **impact your revenue**, because your app may serve less relevant ads.
:::

### Initialize the SDK

By default, the Tapsell SDK **initializes automatically** via a `ContentProvider` when your app starts. This speeds up ad
loading and increases your monetization revenue. No extra code is required.

#### Opting out of auto-initialization

:::info
This feature is available from version [1.4.0-alpha03](/docs/sdk/changelog#1.4.0-alpha03) and later.
:::

<details>
  <summary>Opting out of auto-initialization</summary>

If you need to control when the SDK initializes, set the `ir.tapsell.mediation.AUTO_INIT` manifest flag to `false`:

```xml title="src/main/AndroidManifest.xml"
<manifest>
    <application>
      // highlight-start
        <meta-data
            android:name="ir.tapsell.mediation.AUTO_INIT"
            android:value="false" />
      // highlight-end
    </application>
</manifest>
```

When auto-init is disabled, call `Tapsell.initialize(Context)` manually from your `Application.onCreate()`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
      // highlight-start
        import ir.tapsell.mediation.Tapsell
      // highlight-end

        class MyApplication : Application() {
            override fun onCreate() {
                super.onCreate()
                // highlight-start
                Tapsell.initialize(this)
                // highlight-end
            }
        }
        ```
        ```java
        // highlight-start
        import ir.tapsell.mediation.Tapsell;
        // highlight-end

        public class MyApplication extends Application {
            @Override
            public void onCreate() {
                super.onCreate();
                // highlight-start
                Tapsell.initialize(this);
                // highlight-end
            }
        }
        ```
</details>

### Testing SDK integration

You can set a listener to be notified of the SDK's initialization. The `onInitializationComplete` will be called once
at least one of the mediation adapters are initialized:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="register initialization callback"
        // highlight-next-line
        import ir.tapsell.mediation.Tapsell

        class MainActivity : AppCompatActivity() {
            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_main)

                // highlight-start
                Tapsell.setInitializationListener {
                    // initialization Completed
                }
                // highlight-end
        }
    }

        ```
        ```java title="register initialization callback"
        // highlight-next-line
        import ir.tapsell.mediation.Tapsell;

        public class MainActivity extends AppCompatActivity {
            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_main);
                // highlight-start
                Tapsell.setInitializationListener(() -> {
                    // initialization Completed
                });
                // highlight-end
        }
    }
        ```

## GooglePlay User Privacy Compliance (GDPR)

The Tapsell SDK respects the user privacy policies such as `GDPR`. Considering this policy is required for the apps
published in markets like **GooglePlay**. All ad networks support the user consent policies as well as Tapsell SDK.
The Tapsell Mediation SDK manages the user consent for all implemented ad networks **automatically**.

The correct approach to manage the GDPR consent is showing a **consent dialog** in your application to choose an option
by user. After user selection, you need to pass the user consent result to the Tapsell SDK.
You can pass the result after initialization is completed by adding the following code:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="send user consent to SDK"
        val userConsent: Boolean = true
        Tapsell.setUserConsent(activity, userConsent)
        ```
        ```java title="send user consent to SDK"
        boolean userConsent = true;
        Tapsell.setUserConsent(activity, userConsent);
        ```

:::warning Activity param is optional but:
The `Admob` and `Wortise` ad networks need `Activity` param to manage user consent. If you implement these ad networks
in your project, you **MUST** pass Activity param when sending user consent. Otherwise, you may get runtime errors.
:::

:::tip Avoid showing consent dialog several times:
To avoid showing consent dialog to the user each time, you can store user preferences to your app. But you need to pass
the latest stored user consent to the Tapsell SDK by invoking the above function.
:::

## GooglePlay Family Policy

According to the [GooglePlay Family Policy](https://support.google.com/googleplay/android-developer/answer/9893335?hl=en),
if any of the target audiences for your app is children (especially if youâre developing a game), your appâs content
must be appropriate for these type of users. Also, you are not allowed to collect some personal information like `Google Advertising ID`.
However, the third party advertising SDKs need this advertising id to provide and serve personalized ads for users.

:::warning Google Play Warning
As a result, **in Tapsell SDK all users are treated as 13 or older**. So as an application developer if youâre going to
publish your app in GooglePlay, you need to **express that your app targets audiences with age of 13 or older.**
Otherwise, your app might be removed from GooglePlay according to this policy.
:::

## Next steps

Once you have successfully integrated the Tapsell Mobile Ads SDK, you can proceed to implement your ad formats in the
next page. Choose the ad types that best suits your app.


# اندروید استودیو — تبلیغات بنری

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/ad-formats/banner/

---

# Banner Ads

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/ad-formats/banner

Banner ads are rectangular ads that appear on the device screen usually at the top or bottom.
Banner ads stay on screen while users are interacting with the app, and can refresh automatically after a certain period of time.
If youâre new to mobile advertising, theyâre a great place to start.

This guide shows you how to integrate banner ads from Tapsell into your Android app.

## Prerequisite

1. Follow the SDK integration steps described in [Quick start](../quick-start).
2. Make sure you're running the latest Tapsell Mobile Ads SDK version.

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

## Add Container To Your Layout

The first step toward displaying a banner is to place a _**`BannerContainer`**_ in the layout of the `Activity` or `Fragment` in which youâd like to display it.
The easiest way to do this is to add one to the corresponding `XML` layout file.

Hereâs an example that shows an activityâs _**`BannerContainer`**_:

```xml title="src/main/res/layout/activity_banner.xml"
<!--...-->
<ir.tapsell.mediation.ad.views.banner.BannerContainer
  android:id="@+id/bannerContainerView"
  android:layout_width="wrap_content"
  android:layout_height="wrap_content"
  android:layout_centerHorizontal="true"
  android:layout_alignParentBottom="true">
</ir.tapsell.mediation.ad.views.banner.BannerContainer>
  <!--...-->
```

Alternatively, you can create the _**`BannerContainer`**_ by instantiating it in runtime and adding it to your `ViewGroup`.
The `ViewGroup` can be a `FrameLayout` or a _**`AndroidView`**_ of `Jetpack Compose` application:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="create banner container"
        val container = BannerContainer(context)
        // Add the container to your ViewGroup
        ```
        ```java title="create banner container"
        Container container = new BannerContainer(context);
        // Add the container to your ViewGroup
        ```

To get more information you can check the full example for Jetpack Compose at the bottom of this tutorial.

## Load Ad

Once the BannerContainer is in place, the next step is to load an ad. Banner ads are loaded
by calling the `requestBannerAd` static method of the Tapsell class; passing the **zone ID** and a `RequestResultListener`
to receive the loaded ad id or possible failure notice. This is usually done in the `onCreate()` method of an `Activity`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request method"
        fun requestBannerAd(zoneId: String, bannerSize: BannerSize, listener: RequestResultListener)
        ```
        ```java title="request method"
        public static void requestBannerAd(String zoneId, BannerSize bannerSize, RequestResultListener listener)
        ```

The `RequestResultListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            fun onSuccess(adId: String)
            // Called when there is no ad available
            fun onFailure(message: String)
        }
        ```
        ```java title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            void onSuccess(String adId);
            // Called when there is no ad available
            void onFailure(String message);
        }
        ```

:::warning **Applovin, Unity Ads** warning

If you intent to load Banner ads from **Applovin** ad-network, you also need to pass an `Activity` instance using the method below:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        fun requestBannerAd(zoneId: String, bannerSize: BannerSize, activity: Activity, listener: RequestResultListener)
        ```
        ```java
        public static void requestBannerAd(String zoneId, BannerSize bannerSize, Activity activity, RequestResultListener listener)
        ```

:::

_**`BannerSize`**_ is an optional `enum` parameter determining the size of the shown ad, with the following values:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="supported banner sizes"
        enum class BannerSize {
            BANNER_320_50, // Default value if the parameter is not provided
            BANNER_320_90,
            BANNER_320_100,
            BANNER_250_250,
            BANNER_300_250,
            BANNER_468_60,
            BANNER_728_90,
            BANNER_160_600,
            BANNER_ADAPTIVE
    }
        ```
        ```java title="supported banner sizes"
        enum BannerSize {
            BANNER_320_50, // Default value if the parameter is not provided
            BANNER_320_90,
            BANNER_320_100,
            BANNER_250_250,
            BANNER_300_250,
            BANNER_468_60,
            BANNER_728_90,
            BANNER_160_600,
            BANNER_ADAPTIVE
    }
        ```

Hereâs an example that shows how to load an ad in the `onCreate()` method of an `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/BannerActivity.kt" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.BannerSize
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.Tapsell
        // highlight-end
        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity

        class BannerActivity : AppCompatActivity() {

            companion object {
                // highlight-next-line
                private const val ZONE_ID = "SampleZoneId"
            }
            // highlight-next-line
            private lateinit var mBannerContainer: BannerContainer;

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_banner)

                // highlight-start
                mBannerContainer = findViewById(R.id.bannerContainer);
                Tapsell.requestBannerAd(ZONE_ID, BannerSize.BANNER_320_50, object : RequestResultListener {
                    override fun onSuccess(adId: String) {
                        // Ad loaded
                    }

                    override fun onFailure(message: String) {
                        // Ad not available
                    }
              })
            // highlight-end
            }
        }
        ```
        ```java title="src/main/java/BannerActivity.java" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.BannerSize;
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        import ir.tapsell.mediation.Tapsell;
        // highlight-end

        public class BannerActivity extends AppCompatActivity {

            // highlight-next-line
            private static final String ZONE_ID = "SampleZoneId";
            // highlight-next-line
            private BannerContainer mBannerContainer;

            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_banner);

                // highlight-start
                mBannerContainer = findViewById(R.id.bannerContainer);
                Tapsell.requestBannerAd(ZONE_ID, BannerSize.BANNER_320_50, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        // Ad loaded
                    }

                    @Override
                    public void onFailure(String message); {
                        // Ad not available
                    }
                });
                // highlight-end
            }
        }
        ```

## Show Ad

Once the banner ad is successfully loaded, the next step is to show the ad.
To do so simply call the `showBannerAd()` static method in Tapsell class passing the `adId` received in `onSuccess`
method of the `RequestResultListener`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="show method"
        fun showBannerAd(adId: String, container: BannerContainer, activity: Activity, listener: AdStateListener.Banner)
        ```
        ```java title="show method"
        public static void showBannerAd(String adId, BannerContainer container, Activity activity, AdStateListener.Banner listener)
        ```

The `AdStateListener.Banner` optional parameter can be passed to monitor and handle events related to displaying your
Banner ad. The interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="Banner show callback"
        val listener: AdStateListener.Banner = object: AdStateListener.Banner {

            override fun onAdImpression() {
                // Code to be executed when an impression is recorded for the ad.
            }

            override fun onAdClicked() {
                // Code to be executed when the user clicks on the ad.
            }

            override fun onAdFailed(message: String) {
                // Code to be executed when the ad show fails.
            }
        }
        ```
    ```java title="Banner show callback"
    AdStateListener.Banner listener = new AdStateListener.Banner() {
        @Override
        public void onAdImpression() {
            // Code to be executed when an impression is recorded for the ad.
        }

        @Override
        public void onAdClicked() {
            // Code to be executed when the user clicks on the ad.
        }

        @Override
        public void onAdFailed(String message) {
            // Code to be executed when the ad show fails.
        }
    };
    ```

## Destroy Ad

When you are done showing your banner ad, you should destroy it so that the ad is properly garbage collected.
The example below shows the destroy call in the `onDestroy()` method of an activity:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/BannerActivity.kt"
        import ir.tapsell.mediation.Tapsell
        import androidx.appcompat.app.AppCompatActivity

        class MainActivity: AppCompatActivity() {
            override fun onDestroy() {
                Tapsell.destroyBannerAd(adId)
                super.onDestroy()
            }
        }
        ```
        ```java title="src/main/java/BannerActivity.java"
        import ir.tapsell.mediation.Tapsell;
        import androidx.appcompat.app.AppCompatActivity;

        public class MainActivity extends AppCompatActivity {
            @Override
            public void onDestroy() {
                Tapsell.destroyBannerAd(adId);
                super.onDestroy();
            }
        }
        ```

Hereâs full example for loading and showing an ad in the `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/BannerActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity
        import androidx.annotation.NonNull

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        // highlight-end

        class BannerActivity : AppCompatActivity() {

        // highlight-start
        /**
         * The adId received in `onSuccess` method of the `RequestResultListener`.
         */
        private var adId: String? = null
        // highlight-end

        override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
            setContentView(R.layout.activity_banner)

            // Your logics ...

            requestButton.setOnClickListener { requestAd() }
            showButton.setOnClickListener { showAd() }
        }

        override fun onDestroy() {
            if (adId == null) return
            // highlight-next-line
            Tapsell.destroyBannerAd(adId)
            super.onDestroy()
        }

        private fun requestAd() {
            // highlight-start
            Tapsell.requestBannerAd(ZONE_ID, BannerSize.BANNER_320_50, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    this.adId = adId
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
        // highlight-end
        }

        private fun showAd() {
            if (adId == null) return
            // highlight-start
            Tapsell.showBannerAd(adId, this, object : AdStateListener.Banner {
                override fun onAdImpression() {}

                override fun onAdClicked() {}

                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }
        }
        ```
        ```java title="src/main/java/BannerActivity.java" showLineNumbers
        package ir.tapsell.sample;

        import android.os.Bundle;

        import androidx.annotation.NonNull;
        import androidx.appcompat.app.AppCompatActivity;

        // highlight-start
        import ir.tapsell.mediation.Tapsell;
        import ir.tapsell.mediation.ad.AdStateListener;
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        // highlight-end

        public class BannerActivity extends AppCompatActivity {

            // highlight-start
            /**
             * The adId received in `onSuccess` method of the `RequestResultListener`.
             */
            private String adId;
            // highlight-end

            @Override
            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_banner);

                // Your logics ...

                requestButton.setOnClickListener(v -> requestAd());
                showButton.setOnClickListener(v -> showAd());
            }

            @Override
            public void onDestroy() {
                if (adId == null) return;
                // highlight-next-line
                Tapsell.destroyBannerAd(adId);
                super.onDestroy();
            }

            private void requestAd() {
                // highlight-start
                Tapsell.requestBannerAd(ZONE_ID, BannerSize.BANNER_320_50, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        this.adId = adId;
                    }

                    @Override
                    public void onFailure(String message); {
                        // Failed to request ad
                    }
                });
                // highlight-end
            }

            private void showAd() {
                if (adId == null) return;
                // highlight-start
                Tapsell.showBannerAd(adId, activity, new AdStateListener.Banner() {
                    @Override
                    public void onAdImpression() {}

                    @Override
                    public void onAdClicked() {}

                    @Override
                    public void onAdFailed(@NonNull String message) {}
                    });
                // highlight-end
            }
        }
        ```
        ```kotlin title="src/main/java/BannerActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import android.app.Activity
        import androidx.activity.ComponentActivity
        import androidx.activity.compose.setContent
        import androidx.annotation.NonNull
        import androidx.compose.foundation.layout.*
        import androidx.compose.material.Button
        import androidx.compose.material.Text
        import androidx.compose.runtime.Composable
        import androidx.compose.runtime.DisposableEffect
        import androidx.compose.runtime.mutableStateOf
        import androidx.compose.runtime.remember
        import androidx.compose.ui.Alignment
        import androidx.compose.ui.Modifier
        import androidx.compose.ui.platform.LocalContext
        import androidx.compose.ui.unit.dp
        import androidx.compose.ui.viewinterop.AndroidView

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.ad.request.BannerSize
        import ir.tapsell.mediation.ad.views.banner.BannerContainer
        // highlight-end

        // highlight-next-line
        private const val ZONE_ID = "SampleZoneId"

        class BannerActivity : ComponentActivity() {

            override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
                setContent {
                    BannerScreen()
                }
            }
        }

        @Composable
        fun BannerScreen() {
            // highlight-start
            val adId = remember { mutableStateOf<String?>(null) }
            val adContainer = remember { mutableStateOf<BannerContainer?>(null) }
            // highlight-end
            val activity = LocalContext.current as Activity

            DisposableEffect(Unit) {
                onDispose {
                    // highlight-next-line
                    destroyAd(adId.value)
                }
            }

            Column(
                modifier = Modifier.fillMaxSize().padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Button(onClick = { requestAd { responseId -> adId.value = responseId } }) {
                    Text(text = "Request Ad")
                }

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = { showAd(adId.value, adContainer.value, activity) }, enabled = adId.value != null) {
                    Text(text = "Show Ad")
                }

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = { destroyAd(adId.value) }, enabled = adId.value != null) {
                    Text(text = "Destory Ad")
                }

                // highlight-start
                TapsellBannerView {
                    adContainer.value = it
                }
                // highlight-end
            }
        }

        @Composable
        private fun TapsellBannerView(
            modifier: Modifier = Modifier,
            onUpdate: (BannerContainer) -> Unit = {},
        ) {
            val activity = LocalContext.current as Activity
            val container = BannerContainer(activity)
            AndroidView(
                modifier = modifier.fillMaxWidth(),
                factory = { container }
            )
            onUpdate(container)
        }

        private fun requestAd(onSuccess: (String) -> Unit) {
            // highlight-start
            Tapsell.requestBannerAd(ZONE_ID, BannerSize.BANNER_320_50, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    onSuccess(adId)
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
            // highlight-end
        }

        private fun showAd(adId: String?, container: BannerContainer?, activity: Activity) {
            if (adId == null || container == null) return
            // highlight-start
            Tapsell.showBannerAd(adId, container, activity, object : AdStateListener.Banner {
                override fun onAdImpression() {}
                override fun onAdClicked() {}
                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }

        private fun destroyAd(adId: String?) {
            if (adId == null) return
            // highlight-next-line
            Tapsell.destroyBannerAd(adId)
        }
        ```

## Test Keys

To use test app keys and zones, you can refer to the [Test Keys Link](../test)

## Demo Project

for more info about implementation, you can find Banner sample for `Java`, `Kotlin` and `Jetpack Compose` on GitHub repository.
:::info GitHub Samples
- <SmartLink id={"android.sample.banner.java"}>Java</SmartLink>
- <SmartLink id={"android.sample.banner.kotlin"}>Kotlin</SmartLink>
- <SmartLink id={"android.sample.banner.compose"}>Jetpack Compose</SmartLink>
:::

## FAQ

If you have any problems regarding implementation of the ads in your project, you can refer to the [FAQ link](../faq)


# اندروید استودیو — تبلیغات میان‌صفحه‌ای

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/ad-formats/interstitial/

---

# Interstitial Ads

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/ad-formats/interstitial

Interstitial ads are full-screen ads that cover the interface of their host app. Theyâre typically displayed at natural
transition points in the flow of an app, such as between activities or during the pause between levels in a game. When an app shows an interstitial ad, the user has the choice to either tap on the ad and continue to its destination or close it and return to the app.

This guide shows you how to integrate interstitial ads from Tapsell into your Android app.

## Prerequisite

1. Follow the SDK integration steps described in [Quick start](../quick-start).
2. Make sure you're running the latest Tapsell Mobile Ads SDK version.

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

## Load Ad

Interstitial ads are loaded by calling the `requestInterstitialAd` static method of the Tapsell class; passing the **zone ID** and a `RequestResultListener` to receive the loaded ad id or possible failure notice. This is usually done in
the `onCreate()` method of an `Activity`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request method"
        fun requestInterstitialAd(zoneId: String, listener: RequestResultListener)
        ```
        ```java title="request method"
        public static void requestInterstitialAd(String zoneId, RequestResultListener listener)
        ```

The `RequestResultListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            fun onSuccess(adId: String)
            // Called when there is no ad available
            fun onFailure(message: String)
        }
        ```
        ```java title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            void onSuccess(String adId);
            // Called when there is no ad available
            void onFailure(String message);
        }
        ```

:::warning **Applovin** warning

If you intent to load interstitial ads from **Applovin** ad-network, you also need to pass an `Activity` instance using the method below:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        fun requestInterstitialAd(zoneId: String, activity: Activity, listener: RequestResultListener)
        ```
        ```java
        public static void requestInterstitialAd(String zoneId, Activity activity, RequestResultListener listener)
        ```

:::

Hereâs an example that shows how to load an ad in the `onCreate()` method of an `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/InterstitialActivity.kt" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.Tapsell
        // highlight-end
        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity

        class InterstitialActivity : AppCompatActivity() {

            companion object {
                // highlight-next-line
                private const val ZONE_ID = "SampleZoneId"
            }

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_interstitial)

                // highlight-start
                Tapsell.requestInterstitialAd(ZONE_ID, object : RequestResultListener {
                    override fun onSuccess(adId: String) {
                        // Ad loaded
                    }

                    override fun onFailure(message: String) {
                        // Ad not available
                    }
              })
            // highlight-end
            }
        }
        ```
        ```java title="src/main/java/InterstitialActivity.java" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        import ir.tapsell.mediation.Tapsell;
        // highlight-end

        public class InterstitialActivity extends AppCompatActivity {

            // highlight-next-line
            private static final String ZONE_ID = "SampleZoneId";

            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_interstitial);

                // highlight-start
                Tapsell.requestInterstitialAd(ZONE_ID, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        // Ad loaded
                    }

                    @Override
                    public void onFailure(String message); {
                        // Ad not available
                    }
                });
                // highlight-end
            }
        }
        ```

## Show Ad

Once the interstitial ad is successfully loaded, the next step is to show the ad.
To do so simply call the `showInterstitialAd()` static method in Tapsell class passing the `adId` received in `onSuccess`
method of the `RequestResultListener`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="show method"
        fun showInterstitialAd(adId: String, activity: Activity, listener: AdStateListener.Interstitial)
        ```
        ```java title="show method"
        public static void showInterstitialAd(String adId, Activity activity, AdStateListener.Interstitial listener)
        ```

The `AdStateListener.Interstitial` optional parameter can be passed to monitor and handle events related to displaying your
interstitial ad. The interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="Interstitial show callback"
        val listener: AdStateListener.Interstitial = object: AdStateListener.Interstitial {

            override fun onAdImpression() {
                // Code to be executed when an impression is recorded for the ad.
            }

            override fun onAdClicked() {
                // Code to be executed when the user clicks on the ad.
            }

            override fun onAdClosed(completionState: AdShowCompletionState) {
                // Code to be executed when the full-screen ad is clicked by the user.
            }

            override fun onAdFailed(message: String) {
                // Code to be executed when the ad show fails.
            }
        }
        ```
    ```java title="Interstitial show callback"
    AdStateListener.Interstitial listener = new AdStateListener.Interstitial() {
        @Override
        public void onAdImpression() {
            // Code to be executed when an impression is recorded for the ad.
        }

        @Override
        public void onAdClicked() {
            // Code to be executed when the user clicks on the ad.
        }

        @Override
        public void onAdClosed(AdShowCompletionState completionState) {
            // Code to be executed when the full-screen ad is clicked by the user.
        }

        @Override
        public void onAdFailed(String message) {
            // Code to be executed when the ad show fails.
        }
    };
    ```

The `AdShowCompletionState` passed in `onAdClosed` callback, indicates whether the ad has been **shown completely** or
**skipped** by the user before completion:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="completion state"
        enum class AdShowCompletionState {
            COMPLETED,
            SKIPPED,
            UNKNOWN
        }
        ```
        ```java title="completion state"
        enum AdShowCompletionState {
            COMPLETED,
            SKIPPED,
            UNKNOWN
        }
        ```

Hereâs full example for loading and showing an ad in the `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/InterstitialActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity
        import androidx.annotation.NonNull

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.ad.show.AdShowCompletionState
        // highlight-end

        class InterstitialActivity : AppCompatActivity() {

        // highlight-start
        /**
         * The adId received in `onSuccess` method of the `RequestResultListener`.
         */
        private var adId: String? = null
        // highlight-end

        override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
            setContentView(R.layout.activity_interstitial)

            // Your logics ...

            requestButton.setOnClickListener { requestAd() }
            showButton.setOnClickListener { showAd() }
        }

        private fun requestAd() {
            // highlight-start
            Tapsell.requestInterstitialAd(ZONE_ID, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    this.adId = adId
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
        // highlight-end
        }

        private fun showAd() {
            if (adId == null) return
            // highlight-start
            Tapsell.showInterstitialAd(adId, this, object : AdStateListener.Interstitial {
                override fun onAdImpression() {}

                override fun onAdClosed(@NonNull adShowCompletionState: AdShowCompletionState) {}

                override fun onAdClicked() {}

                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }
        }
        ```
        ```java title="src/main/java/InterstitialActivity.java" showLineNumbers
        package ir.tapsell.sample;

        import android.os.Bundle;

        import androidx.annotation.NonNull;
        import androidx.appcompat.app.AppCompatActivity;

        // highlight-start
        import ir.tapsell.mediation.Tapsell;
        import ir.tapsell.mediation.ad.AdStateListener;
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        import ir.tapsell.mediation.ad.show.AdShowCompletionState;
        // highlight-end

        public class InterstitialActivity extends AppCompatActivity {

            // highlight-start
            /**
             * The adId received in `onSuccess` method of the `RequestResultListener`.
             */
            private String adId;
            // highlight-end

            @Override
            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_interstitial);

                // Your logics ...

                requestButton.setOnClickListener(v -> requestAd());
                showButton.setOnClickListener(v -> showAd());
            }

            private void requestAd() {
                // highlight-start
                Tapsell.requestInterstitialAd(ZONE_ID, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        this.adId = adId;
                    }

                    @Override
                    public void onFailure(String message); {
                        // Failed to request ad
                    }
                });
                // highlight-end
            }

            private void showAd() {
                if (adId == null) return;
                // highlight-start
                Tapsell.showInterstitialAd(adId, activity, new AdStateListener.Interstitial() {
                    @Override
                    public void onAdImpression() {}

                    @Override
                    public void onAdClosed(@NonNull AdShowCompletionState adShowCompletionState) {}

                    @Override
                    public void onAdClicked() {}

                    @Override
                    public void onAdFailed(@NonNull String message) {}
                    });
                // highlight-end
            }
        }
        ```
        ```kotlin title="src/main/java/InterstitialActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import android.app.Activity
        import androidx.activity.ComponentActivity
        import androidx.activity.compose.setContent
        import androidx.annotation.NonNull
        import androidx.compose.foundation.layout.*
        import androidx.compose.material.Button
        import androidx.compose.material.Text
        import androidx.compose.runtime.Composable
        import androidx.compose.runtime.DisposableEffect
        import androidx.compose.runtime.mutableStateOf
        import androidx.compose.runtime.remember
        import androidx.compose.ui.Alignment
        import androidx.compose.ui.Modifier
        import androidx.compose.ui.platform.LocalContext
        import androidx.compose.ui.unit.dp

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.ad.show.AdShowCompletionState
        // highlight-end

        // highlight-next-line
        private const val ZONE_ID = "SampleZoneId"

        class InterstitialActivity : ComponentActivity() {

            override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
                setContent {
                    InterstitialScreen()
                }
            }
        }

        @Composable
        fun InterstitialScreen() {
            // highlight-start
            val adId = remember { mutableStateOf<String?>(null) }
            // highlight-end
            val activity = LocalContext.current as Activity

            Column(
                modifier = Modifier.fillMaxSize().padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Button(onClick = { requestAd { responseId -> adId.value = responseId } }) {
                    Text(text = "Request Ad")
                }

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = { showAd(adId.value, activity) }, enabled = adId.value != null) {
                    Text(text = "Show Ad")
                }
            }
        }

        private fun requestAd(onSuccess: (String) -> Unit) {
            // highlight-start
            Tapsell.requestInterstitialAd(ZONE_ID, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    onSuccess(adId)
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
            // highlight-end
        }

        private fun showAd(adId: String?, activity: Activity) {
            if (adId == null) return
            // highlight-start
            Tapsell.showInterstitialAd(adId, activity, object : AdStateListener.Interstitial {
                override fun onAdImpression() {}

                override fun onAdClosed(@NonNull adShowCompletionState: AdShowCompletionState) {}

                override fun onAdClicked() {}

                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }
        ```

## Test Keys

To use test app keys and zones, you can refer to the [Test Keys Link](../test)

## Demo Project

for more info about implementation, you can find Interstitial sample for `Java`, `Kotlin` and `Jetpack Compose` on GitHub repository.
:::info GitHub Samples
- <SmartLink id={"android.sample.interstitial.java"}>Java</SmartLink>
- <SmartLink id={"android.sample.interstitial.kotlin"}>Kotlin</SmartLink>
- <SmartLink id={"android.sample.interstitial.compose"}>Jetpack Compose</SmartLink>
:::

## FAQ

If you have any problems regarding implementation of the ads in your project, you can refer to the [FAQ link](../faq)


# اندروید استودیو — تبلیغات همسان (بنر و ویدئو)

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/ad-formats/native/

---

# Native Ads (Banner & Video)

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/ad-formats/native

Native ads are ad assets that are presented to users through UI components that are native to the platform.
They are shown using the same types

This guide shows you how to integrate Native ads from Tapsell into your Android app.

## Prerequisite

1. Follow the SDK integration steps described in [Quick start](../quick-start).
2. Make sure you're running the latest Tapsell Mobile Ads SDK version.

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

## Add Container To Your Layout

The first step toward displaying a native ad is to create your ad layout using the same views you use in your
application. Note that the design and format of the ad layout is completely in your own hands to match your app's
design.

The only thing you need to consider in your layout design is that **you must put all your ad layout views inside
a `NativeAdViewContainer`**, provided by Tapsell SDK:

_**`ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer`**_ is a custom Android `FrameLayout` that must be used as the parent view of the native ad item views.
All your ad views providing the content of the ad, like title view, logo view and so on, must be placed inside a **`NativeAdViewContainer`** view.

Here's an example that shows an ad layout inside the activity layout:

```xml title="src/main/res/layout/activity_native.xml"
<ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer
  android:id="@+id/tapsell_native_ad_container"
  ... >

  <!-- ad layout (a layout of your choice) -->
<RelativeLayout
android:layout_width="match_parent"
android:layout_height="match_parent">

<!-- logo view -->
<ImageView
  android:id="@+id/tapsell_native_ad_logo"
... />

<!-- title view -->
<TextView
  android:id="@+id/tapsell_native_ad_title"
... />

<!-- description view -->
<TextView
  android:id="@+id/tapsell_native_ad_description"
... />

<!-- sponsored text view -->
<TextView
  android:id="@+id/tapsell_native_ad_sponsored"
... />

<!-- CTA button view -->
<Button
  android:id="@+id/tapsell_native_ad_cta"
... />

<!-- media view (banner image or video) -->
<FrameLayout
  android:id="@+id/tapsell_native_ad_media"
... />

  </ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer>
```

As an alternative to adding the ad layout directly to the activity layout,
you can only place the **`NativeAdViewContainer`** and have your ad layout in a separate layout file, attaching it programmatically.
See the sample below:

```xml title="src/main/res/layout/activity_native.xml"
<!--...-->
<!-- container -->
<ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer
  android:id="@+id/tapsell_native_ad_container"
  android:layout_width="wrap_content"
  android:layout_height="wrap_content"
  android:layout_centerHorizontal="true"
  android:layout_alignParentBottom="true">
</ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer>
  <!--...-->
```

```xml title="src/main/res/layout/native_ad.xml"
<!-- ad layout (a layout of your choice) -->
<RelativeLayout
  xmlns:android="http://schemas.android.com/apk/res/android"
  android:layout_width="wrap_content"
  android:layout_height="wrap_content">

    <!--  ad content views  -->

```

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/NativeActivity.kt"
        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer

        class NativeActivity : AppCompatActivity() {

            private lateinit var mNativeAdViewContainer: NativeAdViewContainer

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_native)

                mNativeAdViewContainer = findViewById(R.id.tapsell_native_ad_container)
                layoutInflater.inflate(R.layout.native_ad, mNativeAdViewContainer, true)
            }
        }
        ```
        ```java title="src/main/java/NativeActivity.java"
        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity;
        import ir.tapsell.mediation.Tapsell;
        import ir.tapsell.mediation.ad.views.ntv.NativeAdViewContainer;

        public class NativeActivity extends AppCompatActivity {

            private NativeAdViewContainer mNativeAdViewContainer;

                protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_native);

                mNativeAdViewContainer = findViewById(R.id.tapsell_native_ad_container);
                getLayoutInflater().inflate(R.layout.native_ad, mNativeAdViewContainer, true);
            }
        }
        ```

## Load Ad

Once the layout is ready, the next step is to load an ad. Native ads are loaded
by calling the `requestNativeAd` static method of the Tapsell class; passing the **zone ID** and a `RequestResultListener`
to receive the loaded ad id or possible failure notice. This is usually done in the `onCreate()` method of an `Activity`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request method"
        fun requestNativeAd(zoneId: String, listener: RequestResultListener)
        ```
        ```java title="request method"
        public static void requestNativeAd(String zoneId, RequestResultListener listener)
        ```

:::tip Load Multiple Native Ads (Caching)

To cache and get multiple native ads with single request, you can use the `requestMultipleNativeAds` method to fetch and store up to **5** ads at once.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="multiple ads request method"
        fun requestNativeAd(zoneId: String, maximumCount: Int, activity: Activity, listener: RequestResultListener)
        ```
        ```java title="multiple ads request method"
        public static void requestMultipleNativeAds(String zoneId, int maximumCount, Activity activity, RequestResultListener listener)
        ```

This method allows you to simultaneously request multiple ads (up to 5) and invoke the provided listener up to the requested count.
This way, you can preload several ads before displaying them.

You can utilize this feature in infinite scrolling lists or scenarios that require displaying multiple ads. Please note that this functionality is exclusively available for native ads.

You can find the sample project [here](https://github.com/tapsellorg/MediationSample-NativeAd-RecyclerView/blob/master/sample-native-recyclerview/src/main/java/ir/tapsell/mediation/sample/recyclerview/NativeAdProvider.kt#L98)
:::

The `RequestResultListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            fun onSuccess(adId: String)
            // Called when there is no ad available
            fun onFailure(message: String)
        }
        ```
        ```java title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            void onSuccess(String adId);
            // Called when there is no ad available
            void onFailure(String message);
        }
        ```

:::warning **Applovin** warning

If you intent to load Native ads from **Applovin** ad-network, you also need to pass an `Activity` instance using the method below:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        fun requestNativeAd(zoneId: String, activity: Activity, listener: RequestResultListener)
        ```
        ```java
        public static void requestNativeAd(String zoneId, Activity activity, RequestResultListener listener)
        ```

:::

Hereâs an example that shows how to load an ad in the `onCreate()` method of an `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/NativeActivity.kt" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.Tapsell
        // highlight-end
        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity

        class NativeActivity : AppCompatActivity() {

            companion object {
                // highlight-next-line
                private const val ZONE_ID = "SampleZoneId"
            }

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_native)

                // highlight-start
                Tapsell.requestNativeAd(ZONE_ID, object : RequestResultListener {
                    override fun onSuccess(adId: String) {
                        // Ad loaded
                    }

                    override fun onFailure(message: String) {
                        // Ad not available
                    }
              })
            // highlight-end
            }
        }
        ```
        ```java title="src/main/java/NativeActivity.java" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        import ir.tapsell.mediation.Tapsell;
        // highlight-end

        public class NativeActivity extends AppCompatActivity {

            // highlight-next-line
            private static final String ZONE_ID = "SampleZoneId";

            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_native);

                // highlight-start
                Tapsell.requestNativeAd(ZONE_ID, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        // Ad loaded
                    }

                    @Override
                    public void onFailure(String message); {
                        // Ad not available
                    }
                });
                // highlight-end
            }
        }
        ```

## Show Ad

Once the native ad is successfully loaded, the next step is to show the ad. That's done with the `showNativeAd()` static
method in Tapsell class passing the `adId` received in `onSuccess` method of the `RequestResultListener` and an instance
of **`NativeAdView`** populated with the ad views you used in your layout.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="show method"
        fun showNativeAd(adId: String, view: NativeAdView, activity: Activity, listener: AdStateListener.Native)
        ```
        ```java title="show method"
        public static void showNativeAd(String adId, NativeAdView view, Activity activity, AdStateListener.Native listener)
        ```

The **`NativeAdView`** is a class you need to instantiate and populate with your ad views using the `Builder` pattern.
Review the sample below for more details:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="NativeAdView builder"
        NativeAdView.Builder(YOUR_CONTAINER_VIEW) // NativeAdViewContainer
            .withLogo(YOUR_LOGO_VIEW) // ImageView
            .withTitle(YOUR_TITLE_VIEW) // TextView
            .withSponsored(YOUR_SPONSORED_TEXT_VIEW) // TextView
            .withDescription(YOUR_DESCRIPTION_VIEW) // TextView
            .withCtaButton(YOUR_CTA_VIEW) // Button
            .withMedia(YOUR_MEDIA_VIEW) // FrameLayout
            .build()
        ```
        ```java title="NativeAdView builder"
        new NativeAdView.Builder(YOUR_CONTAINER_VIEW) // NativeAdViewContainer
            .withLogo(YOUR_LOGO_VIEW) // ImageView
            .withTitle(YOUR_TITLE_VIEW) // TextView
            .withSponsored(YOUR_SPONSORED_TEXT_VIEW) // TextView
            .withDescription(YOUR_DESCRIPTION_VIEW) // TextView
            .withCtaButton(YOUR_CTA_VIEW) // Button
            .withMedia(YOUR_MEDIA_VIEW) // FrameLayout
            .build()
        ```

:::info Note
depending on your ad design you can only include and provide a subset of the possible ad items and remove the items you don't need.
:::

The `AdStateListener.Native` optional parameter can be passed to monitor and handle events related to displaying your
Native ad. The interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="Native show callback"
        val listener: AdStateListener.Native = object: AdStateListener.Native {

            override fun onAdImpression() {
                // Code to be executed when an impression is recorded for the ad.
            }

            override fun onAdClicked() {
                // Code to be executed when the user clicks on the ad.
            }

            override fun onAdFailed(message: String) {
                // Code to be executed when the ad show fails.
            }
        }
        ```
    ```java title="Native show callback"
    AdStateListener.Native listener = new AdStateListener.Native() {
        @Override
        public void onAdImpression() {
            // Code to be executed when an impression is recorded for the ad.
        }

        @Override
        public void onAdClicked() {
            // Code to be executed when the user clicks on the ad.
        }

        @Override
        public void onAdFailed(String message) {
            // Code to be executed when the ad show fails.
        }
    };
    ```

## Destroy Ad

When you are done showing your Native ad, you should destroy it so that the ad is properly garbage collected.
The example below shows the destroy call in the `onDestroy()` method of an activity:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/NativeActivity.kt"
        import ir.tapsell.mediation.Tapsell
        import androidx.appcompat.app.AppCompatActivity

        class MainActivity: AppCompatActivity() {
            override fun onDestroy() {
                Tapsell.destroyNativeAd(adId)
                super.onDestroy()
            }
        }
        ```
        ```java title="src/main/java/NativeActivity.java"
        import ir.tapsell.mediation.Tapsell;
        import androidx.appcompat.app.AppCompatActivity;

        public class MainActivity extends AppCompatActivity {
            @Override
            public void onDestroy() {
                Tapsell.destroyNativeAd(adId);
                super.onDestroy();
            }
        }
        ```

Hereâs full example for loading and showing an ad in the `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/NativeActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity
        import androidx.annotation.NonNull

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        // highlight-end

        class NativeActivity : AppCompatActivity() {

            // highlight-start
            /**
             * The adId received in `onSuccess` method of the `RequestResultListener`.
             */
            private var adId: String? = null
            private var container: NativeAdViewContainer?
            // highlight-end

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_native)

                // Your logics ...

                // highlight-start
                container = NativeAdViewContainer(this);
                FrameLayout adContainer = findViewById(R.id.banner_container);
                adContainer.addView(container);
                // highlight-end

                requestButton.setOnClickListener { requestAd() }
                showButton.setOnClickListener { showAd() }
                destroyButton.setOnClickListener { destroyAd() }
            }

            override fun onDestroy() {
                destroyAd()
                super.onDestroy()
            }

            private fun requestAd() {
                // highlight-start
                Tapsell.requestNativeAd(ZONE_ID, object : RequestResultListener {
                    override fun onSuccess(@NonNull adId: String) {
                        this.adId = adId
                    }

                    override fun onFailure(message: String) {
                        // Failed to request ad
                    }
                })
            // highlight-end
            }

            private fun showAd() {
                if (adId == null) return
                // highlight-start
                Tapsell.showNativeAd(adId, container, this, object : AdStateListener.Native {
                    override fun onAdImpression() {}

                    override fun onAdClicked() {}

                    override fun onAdFailed(@NonNull message: String) {}
                })
                // highlight-end
            }

            private fun destroyAd() {
                if (adId == null) return
                // highlight-next-line
                Tapsell.destroyNativeAd(adId)
            }
        }
        ```
        ```java title="src/main/java/NativeActivity.java" showLineNumbers
        package ir.tapsell.sample;

        import android.os.Bundle;

        import androidx.annotation.NonNull;
        import androidx.appcompat.app.AppCompatActivity;

        // highlight-start
        import ir.tapsell.mediation.Tapsell;
        import ir.tapsell.mediation.ad.AdStateListener;
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        // highlight-end

        public class NativeActivity extends AppCompatActivity {

            // highlight-start
            /**
             * The adId received in `onSuccess` method of the `RequestResultListener`.
             */
            private String adId;
            private NativeAdViewContainer container;
            // highlight-end

            @Override
            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_native);

                // Your logics ...

                // highlight-start
                container = new NativeAdViewContainer(this);
                FrameLayout adContainer = findViewById(R.id.banner_container);
                adContainer.addView(container);
                // highlight-end

                requestButton.setOnClickListener(v -> requestAd());
                showButton.setOnClickListener(v -> showAd());
                destroyButton.setOnClickListener(v -> destroyAd());
            }

            @Override
            public void onDestroy() {
                destroyAd()
                super.onDestroy();
            }

            private void requestAd() {
                // highlight-start
                Tapsell.requestNativeAd(ZONE_ID, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        this.adId = adId;
                    }

                    @Override
                    public void onFailure(String message); {
                        // Failed to request ad
                    }
                });
                // highlight-end
            }

            private void showAd() {
                if (adId == null) return;
                // highlight-start
                Tapsell.showNativeAd(adId, container, activity, new AdStateListener.Native() {
                    @Override
                    public void onAdImpression() {}

                    @Override
                    public void onAdClicked() {}

                    @Override
                    public void onAdFailed(@NonNull String message) {}
                    });
                // highlight-end
            }

            private void destroyAd() {
                if (adId == null) return
                // highlight-next-line
                Tapsell.destroyNativeAd(adId)
            }
        }
        ```
        ```kotlin title="src/main/java/NativeActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import android.app.Activity
        import androidx.activity.ComponentActivity
        import androidx.activity.compose.setContent
        import androidx.annotation.NonNull
        import androidx.compose.foundation.layout.*
        import androidx.compose.material.Button
        import androidx.compose.material.Text
        import androidx.compose.runtime.Composable
        import androidx.compose.runtime.DisposableEffect
        import androidx.compose.runtime.mutableStateOf
        import androidx.compose.runtime.remember
        import androidx.compose.ui.Alignment
        import androidx.compose.ui.Modifier
        import androidx.compose.ui.platform.LocalContext
        import androidx.compose.ui.unit.dp
        import androidx.compose.ui.viewinterop.AndroidView

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        // highlight-end

        // highlight-next-line
        private const val ZONE_ID = "SampleZoneId"

        class NativeActivity : ComponentActivity() {

            override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
                setContent {
                    NativeScreen()
                }
            }
        }

        @Composable
        fun NativeScreen() {
            // highlight-start
            val adId = remember { mutableStateOf<String?>(null) }
            val adContainer = remember { mutableStateOf<NativeAdViewContainer?>(null) }
            // highlight-end
            val activity = LocalContext.current as Activity

            DisposableEffect(Unit) {
                onDispose {
                    // highlight-next-line
                    destroyAd(adId.value)
                }
            }

            Column(
                modifier = Modifier.fillMaxSize().padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Button(onClick = { requestAd { responseId -> adId.value = responseId } }) {
                    Text(text = "Request Ad")
                }

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = { showAd(adId.value, adContainer.value, activity) }, enabled = adId.value != null) {
                    Text(text = "Show Ad")
                }

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = { destroyAd(adId.value) }, enabled = adId.value != null) {
                    Text(text = "Destory Ad")
                }

                // highlight-start
                TapsellNativeView {
                    adContainer.value = it
                }
                // highlight-end
            }
        }

        @Composable
        private fun TapsellNativeView(
            modifier: Modifier = Modifier,
            onUpdate: (NativeAdViewContainer) -> Unit = {},
        ) {
            val activity = LocalContext.current as Activity
            val container = NativeAdViewContainer(activity)
            AndroidView(
                modifier = modifier.fillMaxWidth(),
                factory = { container }
            )
            onUpdate(container)
        }

        private fun requestAd(onSuccess: (String) -> Unit) {
            // highlight-start
            Tapsell.requestNativeAd(ZONE_ID, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    onSuccess(adId)
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
            // highlight-end
        }

        private fun showAd(adId: String?, container: NativeAdViewContainer?, activity: Activity) {
            if (adId == null || container == null) return
            // highlight-start
            Tapsell.showNativeAd(adId, container, activity, object : AdStateListener.Native {
                override fun onAdImpression() {}
                override fun onAdClicked() {}
                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }

        private fun destroyAd(adId: String?) {
            if (adId == null) return
            // highlight-next-line
            Tapsell.destroyNativeAd(adId)
        }
        ```

## Test Keys

To use test app keys and zones, you can refer to the [Test Keys Link](../test)

## Demo Project

for more info about implementation, you can find Native ad samples for `Java`, `Kotlin` and `Jetpack Compose` on GitHub repository.
:::info GitHub Samples
- <SmartLink id={"android.sample.native.java"}>Java</SmartLink>
- <SmartLink id={"android.sample.native.kotlin"}>Kotlin</SmartLink>
- <SmartLink id={"android.sample.native.compose"}>Jetpack Compose</SmartLink>
:::

## FAQ

If you have any problems regarding implementation of the ads in your project, you can refer to the [FAQ link](../faq)


# اندروید استودیو — تبلیغات PreRoll

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/ad-formats/preroll/

---

# PreRoll Ads

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/ad-formats/preroll

PreRoll ads are the promotional video advertisement that are played before the content of an online video.
They are shown using the same types of views with which you are already building
your layouts, and can be formatted to match your appâs visual design.

This guide shows you how to implement PreRoll ads into your Android app.

:::note Supported Ad Networks
Currently, the only network to support PreRoll ads is **Tapsell legacy** adapter.
:::

## Prerequisite

1. Follow the SDK integration steps described in [Quick start](../quick-start).
2. Make sure you're running the latest Tapsell Mobile Ads SDK version.

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

## Implementation

To integrate pre-roll ads into your Android app, first obtain a VAST ad tag from Tapsell,
then play it through ExoPlayer using either Taproll (recommended) or the IMA extension.

### Get VAST Tag {#get-vast-tag}

:::note Usage
If you're using [Google IMA integration](#play-with-google-ima), skip this section â the SDK fetches
and manages the ad tag internally.
:::

In this method, you can request to get PreRoll VAST tags, but all implementations related to integrating ads with your
video player should be done from your side.
So you can use your own video player (`ExoPlayer` or other video players) here.
but you need to integrate the ad into the player yourself.
Also, you have complete access to your layout and the video player design.

#### 1. Load Ad

PreRoll ads are loaded by calling the `requestPreRollAd` static method of the Tapsell class;
passing the **zone ID** and a `RequestResultListener` to receive the loaded ad id or possible failure notice.
This is usually done in the `onCreate()` method of an `Activity`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
      fun requestPreRollAd(zoneId: String, listener: RequestResultListener)
        ```
        ```java
      public static void  requestPreRollAd(String zoneId, RequestResultListener listener)
        ```

The `RequestResultListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            fun onSuccess(adId: String)
            // Called when there is no ad available
            fun onFailure(message: String)
        }
        ```
        ```java
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            void onSuccess(String adId);
            // Called when there is no ad available
            void onFailure(String message);
        }
        ```

#### 2. Get VAST Tag

Once your ad id is received successfully, the next step is to get VAST tag url of an ad.
Thatâs done with the `getPreRollVastUrl()` static method in `Tapsell` class.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        fun getPreRollVastUrl(adId: String, listener: PreRollAdListener)
        ```
        ```java
        public static void  getPreRollVastUrl(String adId, PreRollAdListener listener)
        ```

The `PreRollAdListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        interface PreRollAdListener {
            // Called when the ad is successfully loaded; providing the VAST tag needed to show the ad
            fun onVastAvailable(adTagUrl: String)
            // Called when there is no VAST tag available
            fun onAdFailed(message: String)
        }
        ```
        ```java
        interface PreRollAdListener {
            // Called when the ad is successfully loaded; providing the VAST tag needed to show the ad
            void onVastAvailable(String adTagUrl);
            // Called when there is no VAST tag available
            void onAdFailed(String message);
        }
        ```

#### 3. Show Ad

Once you have the VAST tag, you can integrate it into your video player. You have two options for ExoPlayer-based integration:

- **[Taproll](#play-with-taproll)** â A lightweight library that implements ExoPlayerâs `AdsLoader` interface directly. No Google IMA SDK dependency.
- **[Google IMA](#play-with-google-ima)** â Uses the Google IMA SDK via the `legacy-ima-extension` adapter.

For other video players, you can implement your own ad integration logic using the VAST tag received in `onVastAvailable` method of the `PreRollAdListener`.

### Play with Taproll {#play-with-taproll}

In this method, you can use the **Taproll** library as a lightweight alternative to the Google IMA.
Taproll implements ExoPlayerâs `AdsLoader` interface directly to handle VAST/VMAP ad loading, playback,
and cue point management â without depending on the Google IMA SDK.

The first step is adding the required dependencies to your projectâs `build.gradle` file:

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

<Tabs
    groupId="gradleType" queryString lazy defaultValue="build.gradle.kts">
        ```kotlin
        dependencies {
            implementation("ir.tapsell:tapsell:$tapsellVersion")
            implementation("ir.tapsell.mediation.adapter:legacy:$tapsellVersion")
            implementation("ir.tapsell.mediation.adapter:legacy-taproll:$tapsellVersion")
        }
        ```
        ```groovy
        dependencies {
            implementation "ir.tapsell:tapsell:$tapsellVersion"
            implementation "ir.tapsell.mediation.adapter:legacy:$tapsellVersion"
            implementation "ir.tapsell.mediation.adapter:legacy-taproll:$tapsellVersion"
        }
        ```

#### 1. Add PlayerView

As with the other methods, you must use `androidx.media3.ui.PlayerView` in your layout.
Hereâs a minimal example:

<details>
    <summary>activity_main.xml</summary>

    ```xml title="src/main/res/layout/activity_main.xml"
    <?xml version="1.0" encoding="utf-8"?>
    <LinearLayout
        xmlns:android="http://schemas.android.com/apk/res/android"
        android:layout_width="match_parent"
        android:layout_height="match_parent"
        android:orientation="vertical"
        android:fitsSystemWindows="true">

        <androidx.media3.ui.PlayerView
            android:id="@+id/player_view"
            android:layout_width="match_parent"
            android:layout_height="match_parent" />

    ```
</details>

:::warning Player Support
The only supported player is **Media3 ExoPlayer**. Other players or Google namespace versions of ExoPlayer are not supported.
:::

#### 2. Create Ad Loader

`TaprollAdLoader` is the main entry point. It implements ExoPlayerâs `AdsLoader` interface
and bridges Taproll ad events to ExoPlayerâs ad playback state. Create it using the builder pattern,
usually in `onCreate()`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        import android.os.Bundle
        import android.util.Log
        import androidx.appcompat.app.AppCompatActivity
        import ir.tapsell.mediation.taproll.exo.TaprollAdLoader

        class MainActivity : AppCompatActivity() {
            private lateinit var adsLoader: TaprollAdLoader

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                // ...

                // Create the Taproll ad loader â replaces the IMA AdsLoader
                adsLoader = TaprollAdLoader.Builder(this)
                    .setAdEventListener { event ->
                        // Handle ad lifecycle events (started, paused, completed, etc.)
                        Log.i("PreRoll", "event ${event.getType()}")
                    }
                    .setAdErrorListener { error ->
                        // Handle ad loading or playback errors
                        Log.e("PreRoll", "error ${error.getMessage()}")
                    }
                    .build()
            }
        }
        ```
        ```java
        import android.os.Bundle;
        import android.util.Log;
        import androidx.appcompat.app.AppCompatActivity;
        import ir.tapsell.mediation.taproll.exo.TaprollAdLoader;

        public class MainActivity extends AppCompatActivity {
            private TaprollAdLoader adsLoader;

            @Override
            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                // ...

                // Create the Taproll ad loader â replaces the IMA AdsLoader
                adsLoader = new TaprollAdLoader.Builder(this)
                    .setAdEventListener(event ->
                        // Handle ad lifecycle events (started, paused, completed, etc.)
                        Log.i("PreRoll", "event " + event.getType()))
                    .setAdErrorListener(error ->
                        // Handle ad loading or playback errors
                        Log.e("PreRoll", "error " + error.getMessage()))
                    .build();
            }
        }
        ```

#### 3. Configure ExoPlayer

Wire the `TaprollAdLoader` into your ExoPlayer instance using
`DefaultMediaSourceFactory.setLocalAdInsertionComponents()`.
This connects Taproll to the player view and ad lifecycle:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        import android.net.Uri
        import androidx.media3.common.MediaItem
        import androidx.media3.datasource.DefaultDataSource
        import androidx.media3.exoplayer.ExoPlayer
        import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
        import androidx.media3.exoplayer.source.MediaSource

        private fun initializePlayer(adTagUrl: String, contentUrl: String) {
            val dataSourceFactory = DefaultDataSource.Factory(this)

            // Set up the ads loader and ad view providers
            val mediaSourceFactory: MediaSource.Factory =
                DefaultMediaSourceFactory(dataSourceFactory)
                    .setLocalAdInsertionComponents({ adsLoader }, binding.playerView)

            // Create an ExoPlayer and set it as the player for content and ads
            player = ExoPlayer.Builder(this)
                .setMediaSourceFactory(mediaSourceFactory)
                .build()

            binding.playerView.player = player
            adsLoader.setPlayer(player)

            // Create the MediaItem with the content URI and ad tag URI
            val adsConfiguration = MediaItem.AdsConfiguration
                .Builder(Uri.parse(adTagUrl))
                .build()
            val mediaItem = MediaItem.Builder()
                .setUri(contentUrl)
                .setAdsConfiguration(adsConfiguration)
                .build()

            // Prepare the content and ad to be played
            player?.setMediaItem(mediaItem)
            player?.prepare()
            // Set PlayWhenReady. If true, playback begins when ready
            player?.playWhenReady = true
        }
        ```
        ```java
        import android.net.Uri;
        import androidx.media3.common.MediaItem;
        import androidx.media3.datasource.DefaultDataSource;
        import androidx.media3.exoplayer.ExoPlayer;
        import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;
        import androidx.media3.exoplayer.source.MediaSource;

        private void initializePlayer(String adTagUrl, String contentUrl) {
            DefaultDataSource.Factory dataSourceFactory = new DefaultDataSource.Factory(this);

            // Set up the ads loader and ad view providers
            MediaSource.Factory mediaSourceFactory =
                new DefaultMediaSourceFactory(dataSourceFactory)
                    .setLocalAdInsertionComponents(() -> adsLoader, binding.playerView);

            // Create an ExoPlayer and set it as the player for content and ads
            player = new ExoPlayer.Builder(this)
                .setMediaSourceFactory(mediaSourceFactory)
                .build();

            binding.playerView.setPlayer(player);
            adsLoader.setPlayer(player);

            // Create the MediaItem with the content URI and ad tag URI
            MediaItem.AdsConfiguration adsConfiguration =
                new MediaItem.AdsConfiguration.Builder(Uri.parse(adTagUrl)).build();
            MediaItem mediaItem = new MediaItem.Builder()
                .setUri(contentUrl)
                .setAdsConfiguration(adsConfiguration)
                .build();

            // Prepare the content and ad to be played
            player.setMediaItem(mediaItem);
            player.prepare();
            // Set PlayWhenReady. If true, playback begins when ready
            player.setPlayWhenReady(true);
        }
        ```

#### 4. Manage Lifecycle

Manage the player and ads loader across lifecycle events to properly save state
and avoid resource leaks:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        import android.os.Build

        class MainActivity : AppCompatActivity() {
            private var player: ExoPlayer? = null
            private lateinit var adsLoader: TaprollAdLoader

            override fun onStart() {
                super.onStart()
                if (Build.VERSION.SDK_INT > 23) {
                    initializePlayer(adTagUrl, contentUrl)
                    binding.playerView.onResume()
                }
            }

            override fun onResume() {
                super.onResume()
                if (Build.VERSION.SDK_INT <= 23 || player == null) {
                    initializePlayer(adTagUrl, contentUrl)
                    binding.playerView.onResume()
                }
            }

            override fun onPause() {
                super.onPause()
                if (Build.VERSION.SDK_INT <= 23) {
                    binding.playerView.onPause()
                    releasePlayer()
                }
            }

            override fun onStop() {
                super.onStop()
                if (Build.VERSION.SDK_INT > 23) {
                    binding.playerView.onPause()
                    releasePlayer()
                }
            }

            override fun onDestroy() {
                // Release ad resources
                adsLoader.release()
                super.onDestroy()
            }

            private fun releasePlayer() {
                // Detach player from ads loader and release
                adsLoader.setPlayer(null)
                binding.playerView.player = null
                player?.release()
                player = null
            }
        }
        ```
        ```java
        import static android.os.Build.VERSION.SDK_INT;

        public class MainActivity extends AppCompatActivity {
            private ExoPlayer player;
            private TaprollAdLoader adsLoader;

            @Override
            public void onStart() {
                super.onStart();
                if (SDK_INT > 23) {
                    initializePlayer(adTagUrl, contentUrl);
                    binding.playerView.onResume();
                }
            }

            @Override
            public void onResume() {
                super.onResume();
                if (SDK_INT <= 23 || player == null) {
                    initializePlayer(adTagUrl, contentUrl);
                    binding.playerView.onResume();
                }
            }

            @Override
            public void onPause() {
                super.onPause();
                if (SDK_INT <= 23) {
                    binding.playerView.onPause();
                    releasePlayer();
                }
            }

            @Override
            public void onStop() {
                super.onStop();
                if (SDK_INT > 23) {
                    binding.playerView.onPause();
                    releasePlayer();
                }
            }

            @Override
            public void onDestroy() {
                // Release ad resources
                adsLoader.release();
                super.onDestroy();
            }

            private void releasePlayer() {
                // Detach player from ads loader and release
                adsLoader.setPlayer(null);
                binding.playerView.setPlayer(null);
                if (player != null) {
                    player.release();
                    player = null;
                }
            }
        }
        ```

### Play with Google IMA integration {#play-with-google-ima}

In this method, you can request to show PreRoll ads and Tapsell SDK will manage all operations related to integrating ad
with your video player using the Google IMA SDK.

The first step toward displaying a PreRoll ad is adding required dependencies
to your projectâs `build.gradle` file as follows:

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

<Tabs
    groupId="gradleType" queryString lazy defaultValue="build.gradle.kts">
        ```kotlin
        dependencies {
            implementation("ir.tapsell:tapsell:$tapsellVersion")
            implementation("ir.tapsell.mediation.adapter:legacy:$tapsellVersion")
            implementation("ir.tapsell.mediation.adapter:legacy-ima-extension:$tapsellVersion")
        }
        ```
        ```groovy
        dependencies {
            implementation "ir.tapsell:tapsell:$tapsellVersion"
            implementation "ir.tapsell.mediation.adapter:legacy:$tapsellVersion"
            implementation "ir.tapsell.mediation.adapter:legacy-ima-extension:$tapsellVersion"
        }
        ```

#### 1. Add Views to Layout

The next step is to create your ad layout using the same views you use in your application.
Note that you can change the video player design according to your requirements.
The only thing you need to consider in your layout design is that you must use `androidx.Media3.PlayerView`.

:::warning Player Support
The only supported player is **Media3 ExoPlayer**. Other players or Google namespace versions of ExoPlayer are no longer supported.
:::

Hereâs an example that shows an ad layout inside the activity layout:

<details>
    <summary>activity_main.xml</summary>

    ```xml title="src/main/res/layout/activity_main.xml"
    <?xml version="1.0" encoding="utf-8"?>
    <androidx.constraintlayout.widget.ConstraintLayout
        xmlns:android="http://schemas.android.com/apk/res/android"
        xmlns:app="http://schemas.android.com/apk/res-auto"
        android:layout_width="match_parent"
        android:layout_height="match_parent">

        <LinearLayout
            android:id="@+id/preroll_container"
            android:layout_width="match_parent"
            android:layout_height="wrap_content"
            android:orientation="vertical"
            app:layout_constraintTop_toBottomOf="@+id/btn_play">

            <RelativeLayout
                android:id="@+id/video_player_container"
                android:layout_width="match_parent"
                android:layout_height="200dp">

                <!-- Your Media3 Player -->
                <androidx.media3.ui.PlayerView
                    android:id="@+id/exo_player"
                    android:layout_width="match_parent"
                    android:layout_height="wrap_content"
                    app:resize_mode="fill"
                    app:shutter_background_color="@android:color/transparent"
                    app:use_controller="true"/>

            <!-- PreRoll Companion Banner -->
            <FrameLayout
                android:id="@+id/companion_container"
                android:layout_width="match_parent"
                android:layout_height="wrap_content"/>

    </androidx.constraintlayout.widget.ConstraintLayout>
    ```
</details>

:::tip Attach Companion Banner
To attach a companion banner ad, consider using a **FrameLayout** which is used for the banner container.
:::

#### 2. Load Ad

Once the layout is ready, the next step is to load an ad.
PreRoll ads are loaded by calling the `requestPreRollAd` static method of the Tapsell class;
passing the following elements:
- **zone ID**
- **container**: a view instance for ad container
- **companionContainer**: an optional view instance for companion banner
- **videoPlayer**: an instance of `PlayerView`
- **videoPath**: video url string
- **listener**: a `RequestResultListener` to receive the loaded ad id or possible failure notice

This is usually done in the `onCreate()` method of an `Activity`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
            fun requestPreRollAd(zoneId: String, container: ViewGroup, companionContainer: ViewGroup?, videoPlayer: FrameLayout, videoPath: String, listener: RequestResultListener)
        ```
        ```java
            public static void  requestPreRollAd(String zoneId, ViewGroup container, @Nullable ViewGroup companionContainer, FrameLayout videoPlayer, String videoPath, RequestResultListener listener)
        ```

The `RequestResultListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            fun onSuccess(adId: String)
            // Called when there is no ad available
            fun onFailure(message: String)
        }
        ```
        ```java
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            void onSuccess(String adId);
            // Called when there is no ad available
            void onFailure(String message);
        }
        ```

#### 3. Show Ad

Once the PreRoll ad is successfully loaded, the next step is to show the ad.
Thatâs done with the `showPreRollAd()` static method in `Tapsell` class passing
the `adId` received in `onSuccess` method of the `RequestResultListener`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        fun showPreRollAd(adId: String, listener: AdStateListener.PreRoll)
        ```
        ```java
        public static void showPreRollAd(String adId, AdStateListener.PreRoll listener)
        ```

The `AdStateListener.PreRoll` optional parameter can be passed to monitor and handle events related to displaying your
PreRoll ad; specially when the user should be PreRoll. The interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        val listener: AdStateListener.PreRoll = object: AdStateListener.PreRoll {

            override fun onAdImpression() {
                // Code to be executed when an impression is recorded for the ad.
            }

            override fun onAdClicked() {
                // Code to be executed when the user clicks on the ad.
            }

            override fun onAdClosed(completionState: AdShowCompletionState) {
                // Code to be executed when the full-screen ad is clicked by the user.
            }

            override fun onVastAvailable(value: String) {
                // Code to be executed when the VAST tag is downloaded
            }

            override fun onAdFailed(message: String) {
                // Code to be executed when the ad show fails.
            }
        }
        ```
        ```java
        AdStateListener.PreRoll listener = new AdStateListener.PreRoll() {
            @Override
            public void onAdImpression() {
                // Code to be executed when an impression is recorded for the ad.
            }

            @Override
            public void onAdClicked() {
                // Code to be executed when the user clicks on the ad.
            }

            @Override
            public void onAdClosed(AdShowCompletionState completionState) {
                // Code to be executed when the full-screen ad is clicked by the user.
            }

            @Override
            public void onVastAvailable(String value) {
                // Code to be executed when the VAST tag is downloaded
            }

            @Override
            public void onAdFailed(String message) {
                // Code to be executed when the ad show fails.
            }
        };
        ```

The `AdShowCompletionState` passed in `onAdClosed` callback, indicates whether the ad has been **shown completely** or
**skipped** by the user before completion:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        enum class AdShowCompletionState {
            COMPLETED,
            SKIPPED,
            UNKNOWN
        }
        ```
        ```java
        enum AdShowCompletionState {
            COMPLETED,
            SKIPPED,
            UNKNOWN
        }
        ```

#### 4. Destroy Ad

When you are done showing your PreRoll ad, you should destroy it so that the ad is properly garbage collected.
The example below shows the destroy call in the `onDestroy()` method of an activity:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        import ir.tapsell.mediation.Tapsell
        import androidx.appcompat.app.AppCompatActivity

        class MainActivity: AppCompatActivity() {
            override fun onDestroy() {
                Tapsell.destroyPreRollAd(adId)
                super.onDestroy()
            }
        }
        ```
        ```java
        import ir.tapsell.mediation.Tapsell;
        import androidx.appcompat.app.AppCompatActivity;

        public class MainActivity extends AppCompatActivity {
            @Override
            public void onDestroy() {
                Tapsell.destroyPreRollAd(adId);
                super.onDestroy();
            }
        }
        ```

## Test Keys

To use test app keys and zones, you can refer to the [Test Keys Link](../test)

## Demo Project

for more info about implementation, you can find PreRoll sample for `Java`, `Kotlin` and `Jetpack Compose` on GitHub repository.
:::info GitHub Samples
- <SmartLink id={"android.sample.preroll.java"}>Java</SmartLink>
- <SmartLink id={"android.sample.preroll.kotlin"}>Kotlin</SmartLink>
- <SmartLink id={"android.sample.preroll.compose"}>Jetpack Compose</SmartLink>
:::

## FAQ

If you have any problems regarding implementation of the ads in your project, you can refer to the [FAQ link](../faq)


# اندروید استودیو — تبلیغات جایزه‌ای

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/ad-formats/rewarded/

---

# Rewarded Ads

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/ad-formats/rewarded

Rewarded ads are full-screen ads that cover the interface of an app until closed by the user. The content of a rewarded
ad might be varying depending on the ad network type in the mediation.

The purpose of a rewarded ad is to reward users with something after completing the video. If the user completes the
action, you can reward them in your app.

This guide shows you how to integrate rewarded ads from Tapsell into your Android app.

## Prerequisite

1. Follow the SDK integration steps described in [Quick start](../quick-start).
2. Make sure you're running the latest Tapsell Mobile Ads SDK version.

:::info latest SDK version
[![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell/tapsell)](https://search.maven.org/artifact/ir.tapsell/tapsell)
:::

## Load Ad

Rewarded ads are loaded by calling the `requestRewardedAd` static method of the Tapsell class; passing the **zone ID** and a `RequestResultListener` to receive the loaded ad id or possible failure notice. This is usually done in
the `onCreate()` method of an `Activity`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request method"
        fun requestRewardedAd(zoneId: String, listener: RequestResultListener)
        ```
        ```java title="request method"
        public static void requestRewardedAd(String zoneId, RequestResultListener listener)
        ```

The `RequestResultListener` interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            fun onSuccess(adId: String)
            // Called when there is no ad available
            fun onFailure(message: String)
        }
        ```
        ```java title="request interface"
        interface RequestResultListener {
            // Called when the ad is successfully loaded; providing the ad id needed to show the ad
            void onSuccess(String adId);
            // Called when there is no ad available
            void onFailure(String message);
        }
        ```

:::warning **Applovin** warning

If you intent to load rewarded ads from **Applovin** ad-network, you also need to pass an `Activity` instance using the method below:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin
        fun requestRewardedAd(zoneId: String, activity: Activity, listener: RequestResultListener)
        ```
        ```java
        public static void requestRewardedAd(String zoneId, Activity activity, RequestResultListener listener)
        ```

:::

Hereâs an example that shows how to load an ad in the `onCreate()` method of an `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/RewardedVideoActivity.kt" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.Tapsell
        // highlight-end
        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity

        class RewardedVideoActivity : AppCompatActivity() {

            companion object {
                // highlight-next-line
                private const val ZONE_ID = "SampleZoneId"
            }

            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                setContentView(R.layout.activity_rewarded_video)

                // highlight-start
                Tapsell.requestRewardedAd(ZONE_ID, object : RequestResultListener {
                    override fun onSuccess(adId: String) {
                        // Ad loaded
                    }

                    override fun onFailure(message: String) {
                        // Ad not available
                    }
              })
            // highlight-end
            }
        }
        ```
        ```java title="src/main/java/RewardedVideoActivity.java" showLineNumbers
        // highlight-start
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        import ir.tapsell.mediation.Tapsell;
        // highlight-end

        public class RewardedVideoActivity extends AppCompatActivity {

            // highlight-next-line
            private static final String ZONE_ID = "SampleZoneId";

            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_rewarded_video);

                // highlight-start
                Tapsell.requestRewardedAd(ZONE_ID, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        // Ad loaded
                    }

                    @Override
                    public void onFailure(String message); {
                        // Ad not available
                    }
                });
                // highlight-end
            }
        }
        ```

## Show Ad

Once the rewarded ad is successfully loaded, the next step is to show the ad.
To do so simply call the `showRewardedAd()` static method in Tapsell class passing the `adId` received in `onSuccess`
method of the `RequestResultListener`.

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="show method"
        fun showRewardedAd(adId: String, activity: Activity, listener: AdStateListener.Rewarded)
        ```
        ```java title="show method"
        public static void showRewardedAd(String adId, Activity activity, AdStateListener.Rewarded listener)
        ```

The `AdStateListener.Rewarded` optional parameter can be passed to monitor and handle events related to displaying your
rewarded ad; specially when the user should be rewarded. The interface has the following implementation:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="Rewarded show callback"
        val listener: AdStateListener.Rewarded = object: AdStateListener.Rewarded {

            override fun onAdImpression() {
                // Code to be executed when an impression is recorded for the ad.
            }

            override fun onAdClicked() {
                // Code to be executed when the user clicks on the ad.
            }

            override fun onAdClosed(completionState: AdShowCompletionState) {
                // Code to be executed when the full-screen ad is clicked by the user.
            }

            override fun onRewarded() {
                // Code to be executed when the user has earned the reward.
            }

            override fun onAdFailed(message: String) {
                // Code to be executed when the ad show fails.
            }
        }
        ```
    ```java title="Rewarded show callback"
    AdStateListener.Rewarded listener = new AdStateListener.Rewarded() {
        @Override
        public void onAdImpression() {
            // Code to be executed when an impression is recorded for the ad.
        }

        @Override
        public void onAdClicked() {
            // Code to be executed when the user clicks on the ad.
        }

        @Override
        public void onAdClosed(AdShowCompletionState completionState) {
            // Code to be executed when the full-screen ad is clicked by the user.
        }

        @Override
        public void onRewarded() {
            // Code to be executed when the user has earned the reward.
        }

        @Override
        public void onAdFailed(String message) {
            // Code to be executed when the ad show fails.
        }
    };
    ```

The `AdShowCompletionState` passed in `onAdClosed` callback, indicates whether the ad has been **shown completely** or
**skipped** by the user before completion:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="video completion state"
        enum class AdShowCompletionState {
            COMPLETED,
            SKIPPED,
            UNKNOWN
        }
        ```
        ```java title="video completion state"
        enum AdShowCompletionState {
            COMPLETED,
            SKIPPED,
            UNKNOWN
        }
        ```

Hereâs full example for loading and showing an ad in the `Activity`:

<Tabs
    groupId="language" queryString lazy defaultValue="Kotlin">
        ```kotlin title="src/main/java/RewardedVideoActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import androidx.appcompat.app.AppCompatActivity
        import androidx.annotation.NonNull

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.ad.show.AdShowCompletionState
        // highlight-end

        class RewardedVideoActivity : AppCompatActivity() {

        // highlight-start
        /**
         * The adId received in `onSuccess` method of the `RequestResultListener`.
         */
        private var adId: String? = null
        // highlight-end

        override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
            setContentView(R.layout.activity_rewarded_video)

            // Your logics ...

            requestButton.setOnClickListener { requestAd() }
            showButton.setOnClickListener { showAd() }
        }

        private fun requestAd() {
            // highlight-start
            Tapsell.requestRewardedAd(ZONE_ID, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    this.adId = adId
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
        // highlight-end
        }

        private fun showAd() {
            if (adId == null) return
            // highlight-start
            Tapsell.showRewardedAd(adId, this, object : AdStateListener.Rewarded {
                override fun onAdImpression() {}

                override fun onAdClosed(@NonNull adShowCompletionState: AdShowCompletionState) {}

                override fun onAdClicked() {}

                override fun onRewarded() {}

                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }
        }
        ```
        ```java title="src/main/java/RewardedVideoActivity.java" showLineNumbers
        package ir.tapsell.sample;

        import android.os.Bundle;

        import androidx.annotation.NonNull;
        import androidx.appcompat.app.AppCompatActivity;

        // highlight-start
        import ir.tapsell.mediation.Tapsell;
        import ir.tapsell.mediation.ad.AdStateListener;
        import ir.tapsell.mediation.ad.request.RequestResultListener;
        import ir.tapsell.mediation.ad.show.AdShowCompletionState;
        // highlight-end

        public class RewardedVideoActivity extends AppCompatActivity {

            // highlight-start
            /**
             * The adId received in `onSuccess` method of the `RequestResultListener`.
             */
            private String adId;
            // highlight-end

            @Override
            protected void onCreate(Bundle savedInstanceState) {
                super.onCreate(savedInstanceState);
                setContentView(R.layout.activity_rewarded_video);

                // Your logics ...

                requestButton.setOnClickListener(v -> requestAd());
                showButton.setOnClickListener(v -> showAd());
            }

            private void requestAd() {
                // highlight-start
                Tapsell.requestRewardedAd(ZONE_ID, new RequestResultListener() {
                    @Override
                    public void onSuccess(@NonNull String adId) {
                        this.adId = adId;
                    }

                    @Override
                    public void onFailure(String message); {
                        // Failed to request ad
                    }
                });
                // highlight-end
            }

            private void showAd() {
                if (adId == null) return;
                // highlight-start
                Tapsell.showRewardedAd(adId, activity, new AdStateListener.Rewarded() {
                    @Override
                    public void onAdImpression() {}

                    @Override
                    public void onAdClosed(@NonNull AdShowCompletionState adShowCompletionState) {}

                    @Override
                    public void onAdClicked() {}

                    @Override
                    public void onRewarded() {}

                    @Override
                    public void onAdFailed(@NonNull String message) {}
                    });
                // highlight-end
            }
        }
        ```
        ```kotlin title="src/main/java/RewardedVideoActivity.kt" showLineNumbers
        package ir.tapsell.sample

        import android.os.Bundle
        import android.app.Activity
        import androidx.activity.ComponentActivity
        import androidx.activity.compose.setContent
        import androidx.annotation.NonNull
        import androidx.compose.foundation.layout.*
        import androidx.compose.material.Button
        import androidx.compose.material.Text
        import androidx.compose.runtime.Composable
        import androidx.compose.runtime.DisposableEffect
        import androidx.compose.runtime.mutableStateOf
        import androidx.compose.runtime.remember
        import androidx.compose.ui.Alignment
        import androidx.compose.ui.Modifier
        import androidx.compose.ui.platform.LocalContext
        import androidx.compose.ui.unit.dp

        // highlight-start
        import ir.tapsell.mediation.Tapsell
        import ir.tapsell.mediation.ad.AdStateListener
        import ir.tapsell.mediation.ad.request.RequestResultListener
        import ir.tapsell.mediation.ad.show.AdShowCompletionState
        // highlight-end

        // highlight-next-line
        private const val ZONE_ID = "SampleZoneId"

        class RewardedVideoActivity : ComponentActivity() {

            override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
                setContent {
                    RewardedVideoScreen()
                }
            }
        }

        @Composable
        fun RewardedVideoScreen() {
            // highlight-start
            val adId = remember { mutableStateOf<String?>(null) }
            // highlight-end
            val activity = LocalContext.current as Activity

            Column(
                modifier = Modifier.fillMaxSize().padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Button(onClick = { requestAd { responseId -> adId.value = responseId } }) {
                    Text(text = "Request Ad")
                }

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = { showAd(adId.value, activity) }, enabled = adId.value != null) {
                    Text(text = "Show Ad")
                }
            }
        }

        private fun requestAd(onSuccess: (String) -> Unit) {
            // highlight-start
            Tapsell.requestRewardedAd(ZONE_ID, object : RequestResultListener {
                override fun onSuccess(@NonNull adId: String) {
                    onSuccess(adId)
                }

                override fun onFailure(message: String) {
                    // Failed to request ad
                }
            })
            // highlight-end
        }

        private fun showAd(adId: String?, activity: Activity) {
            if (adId == null) return
            // highlight-start
            Tapsell.showRewardedAd(adId, activity, object : AdStateListener.Rewarded {
                override fun onAdImpression() {}

                override fun onAdClosed(@NonNull adShowCompletionState: AdShowCompletionState) {}

                override fun onAdClicked() {}

                override fun onRewarded() {}

                override fun onAdFailed(@NonNull message: String) {}
            })
            // highlight-end
        }
        ```

## Test Keys

To use test app keys and zones, you can refer to the [Test Keys Link](../test)

## Demo Project

for more info about implementation, you can find Rewarded sample for `Java`, `Kotlin` and `Jetpack Compose` on GitHub repository.
:::info GitHub Samples
- <SmartLink id={"android.sample.rewarded.java"}>Java</SmartLink>
- <SmartLink id={"android.sample.rewarded.kotlin"}>Kotlin</SmartLink>
- <SmartLink id={"android.sample.rewarded.compose"}>Jetpack Compose</SmartLink>
:::

## FAQ

If you have any problems regarding implementation of the ads in your project, you can refer to the [FAQ link](../faq)


# شبکه‌های تبلیغاتی — Admob

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/admob/

---

# Admob

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/admob

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Admob Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/admob)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/admob)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:admob:$admobVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:admob:$admobVersion"
            }
            ```

3. Open the `app/build.gradle` file from the root of your project and add
the `TapsellMediationAdmobAdapterSignature` key with the ID provided from the [Tapsell Mediation dashboard](https://app.tapsell.ir):
            ```kotlin
            android {
                defaultConfig {
                    addManifestPlaceholders(
                            mapOf("TapsellMediationAdmobAdapterSignature" to "ca-app-pub-xxxxxxxx~xxxxxxxx")
                        )
                    }
            }
            ```
            ```groovy
            android {
                defaultConfig {
                    manifestPlaceholders = [
                        TapsellMediationAdmobAdapterSignature: "ca-app-pub-xxxxxxxx~xxxxxxxx",
                    ]
                }
            }
            ```

    :::danger Note
    Attempting to build your app without configuring a valid App ID for this package
    will cause the app to **crash** on start or fail to build.
    :::

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the Admob ad network returns an error in serving ads,
you can refer to the common Admob error codes listed at the following links:

- [Google support](https://support.google.com/admob/thread/3494603/admob-error-codes-logs?hl=en)
- [Error Codes](https://developers.google.com/admob/android/reference/com/google/android/gms/ads/AdRequest#ERROR_CODE_INTERNAL_ERROR)


# شبکه‌های تبلیغاتی — Applovin

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/applovin/

---

# Applovin

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/applovin

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Applovin Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/applovin)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/applovin)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:applovin:$applovinVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:applovin:$applovinVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the Applovin ad network returns an error in serving ads,
you can refer to the common Applovin error codes listed at the following link:

[Applovin Error Codes](https://developers.applovin.com/en/max/android/overview/error-handling#max-error-codes).


# شبکه‌های تبلیغاتی — ChartBoost

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/chartboost/

---

# ChartBoost

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/chartboost

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell IronSource Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/chartboost)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/chartboost)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:chartboost:$chartboostVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:chartboost:$chartboostVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the chartboost ad network returns an error in serving ads,
you can refer to the common chartboost error codes listed at the following link:

[Chartboost Error Codes](https://docs.chartboost.com/en/monetization/integrate/android/error-codes/).


# شبکه‌های تبلیغاتی — Fyber

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/fyber/

---

# Fyber (Digital Turbine Exchange)

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/fyber

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Fyber Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/fyber)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/fyber)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:fyber:$fyberVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:fyber:$fyberVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the Fyber ad network returns an error in serving ads,
you can refer to the common Fyber error codes listed at the following link:

[Fyber Error Codes](https://developer.digitalturbine.com/hc/en-us/articles/360011618158-SDK-Initialization-for-DT-Exchange-SDK#callback-completion-error-codes-0-2).


# شبکه‌های تبلیغاتی — IronSource

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/ironsource/

---

# IronSource

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/ironsource

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell IronSource Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/ironsource)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/ironsource)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:ironsource:$ironsourceVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:ironsource:$ironsourceVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the IronSource ad network returns an error in serving ads,
you can refer to the common IronSource error codes listed at the following link:

[IronSource Error Codes](https://developers.is.com/ironsource-mobile/Android/supersonic-sdk-error-codes/).


# شبکه‌های تبلیغاتی — Liftoff (Vungle)

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/liftoff/

---

# Liftoff (Vungle)

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/liftoff

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Liftoff Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/liftoff)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/liftoff)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:liftoff:$liftoffVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:liftoff:$liftoffVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the Liftoff ad network returns an error in serving ads,
you can refer to the common Liftoff error codes listed at the following link:

[Liftoff Error Codes](https://support.vungle.com/hc/en-us/articles/15661036969627-Error-Codes-Vungle-SDK-for-iOS-and-Android#h_01HGK2XJFZ927WEH9RVCQM180H).


# شبکه‌های تبلیغاتی — Mintegral

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/mintegral/

---

# Mintegral

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/mintegral

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Mintegral Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/mintegral)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/mintegral)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:mintegral:$mintegralVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:mintegral:$mintegralVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the Mintegral ad network returns an error in serving ads,
you can refer to the common Mintegral error codes listed at the following link:

[Mintegral Error Codes](https://adv-new.mintegral.com/doc/en/guide/appendix/error).


# شبکه‌های تبلیغاتی — Tapsell Legacy

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/tapsellLegacy/

---

# Tapsell Legacy

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/tapsellLegacy

1. Add a dependency on the Tapsell Legacy Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/yandex)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/yandex)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:legacy:$legacyVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:legacy:$legacyVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs


# شبکه‌های تبلیغاتی — UnityAds

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/unityads/

---

# UnityAds

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/unityads

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell UnityAds Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/unityads)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/unityads)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:unityads:$unityadsVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:unityads:$unityadsVersion"
            }
            ```

## Test IDs

### Test App Keys

## Error Codes

If the UnityAds ad network returns an error in serving ads,
you can refer to the common UnityAds error codes listed at the following link:

[UnityAds Error Codes](https://docs.unity3d.com/Packages/com.unity.ads@3.7/api/UnityEngine.Advertisements.UnityAdsLoadError.html).


# شبکه‌های تبلیغاتی — Wortise

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/wortise/

---

# Wortise

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/wortise

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Wortise Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/wortise)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/wortise)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:wortise:$wortiseVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:wortise:$wortiseVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes


# شبکه‌های تبلیغاتی — Yandex

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/adnetworks/yandex/

---

# Yandex

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/adnetworks/yandex

1. <MavenRepositoriesDescription/>

2. Add a dependency on the Tapsell Yandex Mediation Adapter to a Gradle file at your app's `build.gradle` file:

    :::info latest SDK version
    [![Maven Central](https://img.shields.io/maven-central/v/ir.tapsell.mediation.adapter/yandex)](https://central.sonatype.com/artifact/ir.tapsell.mediation.adapter/yandex)
    :::

            ```kotlin
            dependencies {
                implementation("ir.tapsell.mediation.adapter:yandex:$yandexVersion")
            }
            ```
            ```groovy
            dependencies {
                implementation "ir.tapsell.mediation.adapter:yandex:$yandexVersion"
            }
            ```

## Test IDs

### Test App Keys

### Test Zone IDs

## Error Codes

If the Yandex ad network returns an error in serving ads,
you can refer to the common Yandex error codes listed at the following link:

[Yandex Error Codes](https://ads.yandex.com/helpcenter/en/support/faq/sdk-errors).


# تست تبلیغات (اندروید)

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/test/

---

# تست تبلیغات

برای اطمینان از صحت پیاده‌سازی تبلیغات تپسل یا سایر شبکه‌های تبلیغاتی، می‌توانید از ZoneID‌های تست در جدول زیر استفاده کنید.

- برای بررسی عملکرد شبکه‌های تبلیغاتی که اضافه کرده‌اید، از ZoneId تست مربوط به هر کدام استفاده کنید. هر ZoneID به یک شبکه تبلیغاتی و یک نوع تبلیغ اشاره دارد و تبلیغ در حالت آزمایشی نمایش داده می‌شود.

- در حالت تست باید از کلید مانیفست تست استفاده کنید.

- کلیدهای تست را در بیلد ریلیز (Release Build Variant) نیز بررسی کنید.

هشدار پیش از انتشار اپ در مارکت
اطمینان حاصل کنید که پیش از قرار دادن برنامه در مارکت، کلید اپلیکیشن و شناسه‌های جایگاه تبلیغاتی را با شناسه واقعی که در داشبورد تپسل دریافت کرده‌اید، جایگزین کرده باشید.

### کلیدهای تست​

| | | Ad Network | | Key |
| Tapsell | 76798342-99a7-4a5f-bf5a-60a088d5dcfb |
| Admob | ca-app-pub-3940256099942544~3347511713 |

### شناسه جا یگاه های تبلیغاتی تست​

| | | Ad Network | | Ad Type | | ZoneId |
| All AdNetworks | Rewarded Video | 1d710cc7-5e96-46ac-a3e9-8463300333e6 |
| All AdNetworks | Interstitial | b3972749-f62a-475a-9ff2-cfc9e2a40f87 |
| All AdNetworks | Native | d217e3e6-0070-4120-925d-5d39d0298893 |
| All AdNetworks | Banner | e3d5999c-5990-4e31-8ce9-642ce040a7f4 |
| All AdNetworks | PreRoll | 6977a96f-e265-4895-b219-33bb6ba3df85 |
| Legacy | Rewarded Video | 63b92f04-3d0f-4805-84a4-abeccf4edc18 |
| Legacy | Interstitial | 0c4e2849-feea-4688-8280-0b0ae2ee0728 |
| Legacy | Native | b1b92bca-8a54-4b82-9f53-90ea2b1a912c |
| Legacy | Banner | 4bf4e11d-0967-41e6-91f6-a128d9462f2a |
| Legacy | PreRoll | ca7355fa-2b59-436f-9f73-70b408d54b6a |
| Admob | Rewarded Video | d8f4ff72-e2e9-4e67-9eb8-f6d75787ef09 |
| Admob | Interstitial | 48b73764-8025-4c9d-9507-ce7a2c7f32ef |
| Admob | Native | 15cacb1b-6598-4fe9-b2da-8b26b4c1bbc5 |
| Admob | Banner | f965455e-a37a-4732-a0b4-05fc39cae16e |
| Fyber | Rewarded Video | 42bd8edf-bdc9-4da5-ae6d-d465366b29f4 |
| Fyber | Interstitial | ec274046-5cc7-40e0-973b-405084682967 |
| Fyber | Banner | 10365955-147e-4fbc-9a08-15a930797902 |
| Applovin | Rewarded Video | 1abf9ca9-4f93-4ead-a238-b2d0d3032a7a |
| Applovin | Interstitial | 8c33dc60-5911-4145-90b0-d8cce9594fed |
| Applovin | Native | 2db08af5-54e3-458e-9399-1d365a7516c9 |
| Applovin | Banner | 1df4bf1e-4fef-4704-b776-7881bdad5303 |
| Mintegral | Rewarded Video | b4c24eba-b715-47aa-8e6e-641d19936765 |
| Mintegral | Interstitial | b2ef073b-f9a1-4555-8450-ce30cf3cee98 |
| Mintegral | Native | 1a744a71-5abe-4d4a-8323-3832c89495eb |
| Mintegral | Banner | 6c0b2878-c743-4668-b224-00b07ed66550 |
| IronSource | Rewarded Video | 59ea106a-10c2-4711-9b50-38c73200f56a |
| IronSource | Interstitial | e87e094b-1e51-468c-ba0c-b2752dc04d72 |
| IronSource | Banner | 33063aae-07c0-4571-aeac-842c0c7f6478 |
| Liftoff (Vungle) | Rewarded | 0f447ea1-6b11-4e39-b942-712e2d696d78 |
| Liftoff (Vungle) | Interstitial | 2316460a-fbcc-4e29-8a8e-f10ef5358b3b |
| Liftoff (Vungle) | Banner | c066375e-da10-45a8-be3b-6edf5668a0e4 |
| Liftoff (Vungle) | Native | 8ba28550-a749-4226-a4dc-81191949ec88 |
| ChartBoost | Rewarded | 3dde311c-daf5-4a6b-8ce7-08a4103cfb7f |
| ChartBoost | Interstitial | 0d3e6f6d-8a11-4c32-a076-4415c03132a9 |
| ChartBoost | Banner | 0d3e6f6d-8a11-4c32-a076-4415c03132a9 |
| Yandex | Rewarded Video | 27891b5b-1ff5-48f2-98ad-096cfc2dda1a |
| Yandex | Interstitial | e0695171-a079-40c6-9dce-1b31ac2bce15 |
| Yandex | Native | 26ac2ef1-5968-43c2-889b-bf36d68368c4 |
| Yandex | Banner | d088d32c-47ce-4df8-9b2c-184716c77ec0 |
| Wortise | Rewarded | 4fcf71a4-afd1-412d-9b19-46dd85644f9d |
| Wortise | Interstitial | 4d268df4-7e8a-43d8-a014-02b4aeff1e72 |
| Wortise | Native | 88eab80b-113b-4856-8dc2-4aa571c4d7c3 |
| Wortise | Banner | ddd6c321-1f1f-4396-8524-a4f760063b2f |

# سوالات رایج (اندروید)

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/faq/

---

(این صفحه در نسخه‌ی فعلی مستندات فقط یک صفحه‌ی فهرست است و محتوای مستقلی ندارد؛ سوالات عمومی SDK را در بخش «سوالات رایج SDK» همین فایل ببینید.)

# مهاجرت از تپسل پلاس

URL: https://developer.tapsell.ir/fa/docs/sdk/platforms/android/tapsell-plus-migration-guide/

---

# Migration Guide From Tapsell Plus

> Source: https://developer.tapsell.ir/docs/sdk/platforms/android/tapsell-plus-migration-guide

The docs below shows how to migrate from Tapsell Plus SDK to new Mediation SDK

## Replace dependencies

|                      | Old                                                                                                                                                   | New                                                                  |
|----------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------|
| Tapsell              | ir.tapsell.plus:tapsell-plus-sdk-android                                                                                                              | ir.tapsell:tapsell<br/>ir.tapsell.mediation.adapter:legacy           |
| Admob                | com.google.android.gms:play-services-ads                                                                                                              | ir.tapsell.mediation.adapter:admob                                   |
| Mintegral            | com.mbridge.msdk.oversea:mbbanner<br/>com.mbridge.msdk.oversea:reward<br/>com.mbridge.msdk.oversea:newinterstitial<br/>com.mbridge.msdk.oversea:mbbid | ir.tapsell.mediation.adapter:mintegral                               |
| Applovin             | com.applovin:applovin-sdk                                                                                                                             | ir.tapsell.mediation.adapter:applovin                                |
| Unity Ads            | com.unity3d.ads:unity-ads                                                                                                                             | ir.tapsell.mediation.adapter:unity                                   |
| ChartBoost           | com.chartboost:chartboost-sdk                                                                                                                         | ir.tapsell.mediation.adapter:chartboost                              |
| New <br/>Ad Networks | -                                                                                                                                                     | [All Ad Networks](./adnetworks)                              |

## Manifest placeholders in Gradle
|         | Old                                                    | New                                                                                                                       |
|---------|--------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| Tapsell | Handled by `TapsellPlus.initialize`                    | [TapsellMediationAppKey](./quick-start#set-up-your-app)                              |
| Admob   | Handled by `com.google.android.gms.ads.APPLICATION_ID` | [TapsellMediationAdmobAdapterSignature](./adnetworks/admob)                          |

example:
```kotlin
android {
    defaultConfig {
        addManifestPlaceholders(
            mapOf(
                  "TapsellMediationAppKey" to "YOUR_APP_ID",
                  "TapsellMediationAdmobAdapterSignature" to "YOUR_ADMOB_APP_ID" // if you implemented admob adapter
            )
        )
    }
}
```

## Methods replacements

|              | Old                                 | New                                                                                                                  |
|--------------|-------------------------------------|----------------------------------------------------------------------------------------------------------------------|
| Initialize   | TapsellPlus.initialize              | [Will be initialized automatically](./quick-start#initialize-the-sdk)           |
| GDPR         | TapsellPlus.setGDPRConsent          | [Tapsell.setUserConsent](./quick-start#googleplay-user-privacy-compliance-gdpr) |
| Rewarded     | TapsellPlus.requestRewardedVideoAd  | [Tapsell.requestRewardedAd](./ad-formats/rewarded/#load-ad)                     |
| Rewarded     | TapsellPlus.showRewardedVideoAd     | [Tapsell.showRewardedAd](./ad-formats/rewarded/#show-ad)                        |
| Interstitial | TapsellPlus.requestInterstitialAd   | [Tapsell.requestInterstitialAd](./ad-formats/interstitial/#load-ad)             |
| Interstitial | TapsellPlus.showInterstitialAd      | [Tapsell.showInterstitialAd](./ad-formats/interstitial/#show-ad)                |
| Banner       | TapsellPlus.requestStandardBannerAd | [Tapsell.requestBannerAd](./ad-formats/banner/#load-ad)                         |
| Banner       | TapsellPlus.showStandardBannerAd    | [Tapsell.showBannerAd](./ad-formats/banner/#show-ad)                            |
| Banner       | TapsellPlus.destroyStandardBanner   | [Tapsell.destroyBannerAd](./ad-formats/banner/#destroy-ad)                      |
| Native       | TapsellPlus.requestNativeBannerAd   | [Tapsell.requestNativeAd](./ad-formats/native/#load-ad)                         |
| Native       | TapsellPlus.showNativeBannerAd      | [Tapsell.showNativeAd](./ad-formats/native/#show-ad)                            |
| Native       | TapsellPlus.requestNativeVideoAd    | [Tapsell.requestNativeAd](./ad-formats/native/#load-ad)                         |
| Native       | Not Implemented                     | [Tapsell.requestMultipleNativeAds](./ad-formats/native/#load-ad)                |
| Native       | TapsellPlus.showNativeVideoAd       | [Tapsell.showNativeAd](./ad-formats/native/#show-ad)                            |
| Native       | TapsellPlus.destroyNativeBanner     | [Tapsell.destroyNativeAd](./ad-formats/native/#destroy-ad)                      |
| PreRoll      | TapsellPlus.getVastTag              | [Tapsell.requestPreRollAd](./ad-formats/preroll/#load-ad)                       |
| PreRoll      | Not Implemented                     | [Tapsell.showPreRollAd](./ad-formats/preroll/#show-ad)                          |

## Demo Project

for more info about implementation, you can find Banner sample for `Java`, `Kotlin` and `Jetpack Compose` on GitHub repository.
:::info GitHub Samples
- <SmartLink id={"android.sample.java"}>Java</SmartLink>
- <SmartLink id={"android.sample.kotlin"}>Kotlin</SmartLink>
- <SmartLink id={"android.sample.compose"}>Jetpack Compose</SmartLink>
:::


# جدول سازگاری

URL: https://developer.tapsell.ir/fa/docs/sdk/compatibility-matrix/

---

# Compatibility Matrix Table

> Source: https://developer.tapsell.ir/docs/sdk/compatibility-matrix

The tables below shows the versions mapping between Mediation SDK and Tapsell internal plugin versions.

## Internal Plugins

| Tapsell Mediation Version | Gradle Version | Kotlin Version |
|:-------------------------:|:--------------:|:--------------:|
|          `1.1.0`          |    `7.6.1`     |    `1.8.20`    |

## Internal Ad Networks

| Tapsell Mediation Version | Tapsell Legacy Version | Admob Version |
|:-------------------------:|:----------------------:|:-------------:|
|          `1.1.0`          |        `4.9.10`        |   `22.6.0`    |


# تغییرات نسخه‌ها

URL: https://developer.tapsell.ir/fa/docs/sdk/changelog/

---

# Changelog

> Source: https://developer.tapsell.ir/docs/sdk/changelog

## Version 1.4 {#1.4}

### 1.4.0-alpha04 {#1.4.0-alpha04}
Published in 2026-10-03
#### Common {#1.4.0-alpha04-common}
- Supported the legacy `24-character` format as the app key.
- Upgraded Taproll to `1.2`:
  - Minor improvement on proguard rules.
#### Unity {#1.4.0-alpha04-unity}
- Added **Disable Auto Init** setting under the **Advanced** section of the Tapsell Mediation settings window. When
  enabled, the SDK no longer initializes automatically.
- Added `Tapsell.Initialize()` method to manually initialize the SDK when the **Disable Auto Init** setting is enabled.
- Settings changes are now applied only when pressing the **Save** button in the settings window, instead of
  automatically when the window closes.
#### React Native {#1.4.0-alpha04-reactnative}
- No release.
#### Flutter {#1.4.0-alpha04-flutter}
- No release.

### 1.4.0-alpha03 {#1.4.0-alpha03}
Published in 2026-08-08
#### Common {#1.4.0-alpha03-common}
- Added `ir.tapsell.mediation.AUTO_INIT` manifest flag to opt out of automatic ContentProvider-based SDK
  initialization. When set to `false`, publishers must call `Tapsell.initialize(Context)` manually from
  `Application.onCreate()`. This feature is only available on the Android-native flavor (not Flutter, React Native,
  or Unity).
- Supported `secret-key` as an alternative to UUID-based `app-id` in the `AndroidManifest` meta-data.
- Upgraded `Legacy` adapter to `4.9.16`:
  - Fixed `NetworkOnMainThread` exception when closing rewarded ads.
- `Legacy-Taproll` improvements:
  - Redesigned overlay API: replaced fixed-view `AdOverlay` with lifecycle-based interface, added `AdInfo`
    data class, and `ViewOverlayAdapter` for default look.
  - Fixed midroll pod index mapping and stale ad overlay on backward seek.
- Fixed rewarded ad completion state being reported incorrectly when the ad is closed.

#### Unity {#1.4.0-alpha03-unity}
- No platform-specific changes.
#### React Native {#1.4.0-alpha03-reactnative}
- Fixed namespace collision with the native mediation SDK.
- Fixed tapsell imports to resolve from source instead of the built lib.
- Updated the project to align with the React Native `0.77` templates.
#### Flutter {#1.4.0-alpha03-flutter}
- Removed unused dependencies from some packages, including `analyzer` and
  `plugin_platform_interface`. This fixes the `analyzer` version conflict with packages
  such as `freezed` 3.x, as reported in [20](https://github.com/tapsellorg/Discussion/discussions/20).

### 1.4.0-alpha02 {#1.4.0-alpha02}
Published in 2026-06-08
#### Common {#1.4.0-alpha02-common}
- Added **Taproll (Tapsell's in-stream ads SDK)** support as an alternative for Google IMA for serving preroll video
  ads. <SmartLink id="blog.taproll">More info</SmartLink>.
- Log installed and active ad networks on SDK startup to aid debugging during development.
- Improve time synchronization reliability when internet access is restricted.
- Fix a brief visual flash when closing rewarded video ads.
#### Unity {#1.4.0-alpha02-unity}
- Fixed an issue where native ads failed to show; The managed code linker was stripping auto-initialization from
  the `Admob` and `Legacy` adapters.
- Improved rendering of portrait images in native ads of the `Legacy` adapter.
- Sample project upgrade and improvements:
  - Improved RTL sample of Native Ad scenes.
  - Migrated **EDM4U** from bundled `1.2.186` DLLs to UPM package (`com.google.external-dependency-manager`).
  - Upgraded **TextMesh Pro** (TMP) package.
  - Migrated **RTLTMPro** from bundled assets to UPM package (`com.nosuchstudio.rtltmpro`), removed old source files.
  - Added in-game debug console (`com.yasirkula.ingamedebugconsole`) to sample scenes for runtime log inspection.
  - Added **Android Logcat** (`com.unity.mobile.android-logcat`) for viewing device logs in Unity Editor.
  - Upgraded other Unity packages (collab-proxy, IDE tools, uGUI to latest version).
#### React Native {#1.4.0-alpha02-reactnative}
- No release
#### Flutter {#1.4.0-alpha02-flutter}
- No release

### 1.4.0-alpha01 {#1.4.0-alpha01}
Published in 2026-03-13
#### Common {#1.4.0-alpha01-common}
- Support [16KB page size](https://developer.android.com/guide/practices/page-sizes) for native libs
  to Android 15+ compatibility on 64-bit devices.
- A lot of optimizations has applied on core functionalities.
#### Unity {#1.4.0-alpha01-unity}
**Unity Editor:**
- New Adapter management in the **Tapsell Settings Window** (`Assets > Tapsell Mediation > Settings`):
  - Enable or disable each ad network adapter (Legacy, Admob, Applovin, Chartboost, Fyber, IronSource,
    Liftoff, Mintegral, UnityAds, Wortise, Yandex) without manually editing XML files.
  - Zone type support table per adapter (Rewarded, Interstitial, Banner, Native, PreRoll).
  - Driver installation status for adapters that require a separate driver package (`Admob`, `Legacy`), with a direct
    install link.
- `TapsellMediationDependencies.xml` is now dynamically regenerated based on enabled adapters, so only selected networks
  are resolved by EDM4U.
- SDK modules are now distributed as pre-compiled DLLs instead of C# source files.
- Tapsell keys are now stored in `ProjectSettings/TapsellMediationSettings.json` to simplify upgrading the package
  without losing configurations.
  - [Breaking - Action required]: Re-enter keys in the **Tapsell Settings Window**
- Traditional `<meta-data>` tags in custom `AndroidManifest.xml` have been removed. Keys are now provided via
  `manifestPlaceholders` in `Assets/Plugins/Android/launcherTemplate.gradle`. This is done automatically, but
  `Assets -> Tapsell Mediation -> Force Refresh Keys` is available too.
  - [Breaking - Action required]: Enable **Custom Launcher Gradle Template** in **Player Settings** if it is not
    already enabled.
  - **Custom Main Manifest** is no longer required by **Tapsell Mediation SDK**.
- Introduced `maven.tapsell.ir` repo in packages.
- Add fast access items in `Assets > Tapsell Mediation` to documentations and changelogs.
- Provide package version via `Tapsell` class, also show it in Tapsell setting window

**SDK:**
- Optimize loading images in native-ads of `Legacy` adapter.
#### React Native {#1.4.0-alpha01-reactnative}
- No release
#### Flutter {#1.4.0-alpha01-flutter}
- No release

## Version 1.3 {#1.3}

### 1.3.0 {#1.3.0}
Published in 2026-04-20

This release is identical to [1.3.0-beta06](#1.3.0-beta06). Below is the full list of changes since [1.2.0](#1.2.0):
#### Common {#1.3.0-common}
- Upgraded Tapsell Legacy adapter to [4.9.15](/docs/sdk-legacy/platforms/android/changelog#4.9.15).
  - Prevented screen from turning off during video ad playback.
  - Fixed a rare rewarded-ad crash on unstable network connections.
  - Added Android 15 [edge-to-edge](https://developer.android.com/develop/ui/views/layout/edge-to-edge) compatibility.
  - Improved R8 fullMode compatibility.
- Bump `compile-sdk` version into `35`.
- Improved reliability in network restrictions.
- Reduced adapter dependency conflicts in `admob`, `applovin`, `chartboost`, `ironsource`, `wortise`, `yandex` adapters.
- Migrated `ironsource` to new dependency `com.unity3d.ads-mediation:mediation-sdk` to be resolvable from
  `MavenCentral`.
- Reduced the size of SDK.
- Minor internal improvements.
#### Unity {#1.3.0-unity}
- Refactored `gradle.properties` processor to improve reliability and handle file modifications more safely.
- Improved error handling in native-ads.
- Introduced `maven.tapsell.ir` repo in packages [backport from #1.4.0-alpha01]
#### React Native {#1.3.0-reactnative}
- No release
#### Flutter {#1.3.0-flutter}
- Renamed `tapsell_legacy_adapter` into `tapsell_mediation_legacy` for native ads using `legacy` adapter.
- Introduced `maven.tapsell.ir` repo in packages [backport from #1.4.0-alpha01]
- Update `google_mobile_ads` dependency to version `5.2.0` in the `admob` adapter package.
- Upgrade Android development toolchain including Gradle, Kotlin, and Mockito to their latest stable versions.
- Migrate all Android build scripts from Groovy to Kotlin DSL and consolidate them into a unified configuration
  structure.

### 1.3.0-beta06 {#1.3.0-beta06}
Published in 2026-02-10
#### Common {#1.3.0-beta06-common}
- Upgraded Tapsell Legacy adapter to [4.9.15](/docs/sdk-legacy/platforms/android/changelog#4.9.15).
  - Prevented screen from turning off during video ad playback.
  - Fixed a rare rewarded-ad crash on unstable network connections
  - Added Android 15 [edge-to-edge](https://developer.android.com/develop/ui/views/layout/edge-to-edge) compatibility
#### Unity {#1.3.0-beta06-unity}
- No platform-specific changes.
#### React Native {#1.3.0-beta06-reactnative}
- Introduced `maven.tapsell.ir` repo in packages [backport from #1.4.0-alpha01]
#### Flutter {#1.3.0-beta06-flutter}
- Introduced `maven.tapsell.ir` repo in packages. [backport from #1.4.0-alpha01]
- Update `google_mobile_ads` dependency to version `5.2.0` in the `admob` adapter package.
- Upgrade Android development toolchain including Gradle, Kotlin, and Mockito to their latest stable versions.
- Migrate all Android build scripts from Groovy to Kotlin DSL and consolidate them into a unified configuration
  structure.

### 1.3.0-beta05 {#1.3.0-beta05}
Published in 2026-02-07
#### Common {#1.3.0-beta05-common}
- Optimize reliability mechanisms in network restrictions.
#### Unity {#1.3.0-beta05-unity}
- No platform-specific changes.
#### React Native {#1.3.0-beta05-reactnative}
- No release
#### Flutter {#1.3.0-beta05-flutter}
- No release

### 1.3.0-beta04 {#1.3.0-beta04}
Published in 2026-01-31
#### Common {#1.3.0-beta04-common}
- Improved reliability in network restrictions.
#### Unity {#1.3.0-beta04-unity}
- No platform-specific changes.
#### React Native {#1.3.0-beta04-reactnative}
- No release
#### Flutter {#1.3.0-beta04-flutter}
- No release

### 1.3.0-beta03 {#1.3.0-beta03}
Published in 2026-01-28
#### Common {#1.3.0-beta03-common}
- Bump `compile-sdk` version into `35`.
- Prevent the screen from turning off during video ad playback.
- Minor internal improvements.
#### Unity {#1.3.0-beta03-unity}
- Refactor `gradle.properties` processor to improve reliability and handle file modifications more safely.
- Improve error handling in native-ads.
#### React Native {#1.3.0-beta03-reactnative}
- No release
#### Flutter {#1.3.0-beta03-flutter}
- No release

### 1.3.0-beta02 {#1.3.0-beta02}
Published in 2025-11-17
#### Common {#1.3.0-beta02-common}
- Upgraded Tapsell Legacy adapter to [4.9.14](/docs/sdk-legacy/platforms/android/changelog#4.9.14).
  - Improved R8 fullMode compatibility.
- Reduce adapters dependency to remove its dependency conflict in some edge-cases in `admob`, `applovin`, `chartboost`,
  `ironsource` , `wortise`, `yandex` adapters.
- Migrate `ironsource` to new dependency `com.unity3d.ads-mediation:mediation-sdk` to be resolvable from `MavenCentral`.
- Minor internal improvements.
#### Unity {#1.3.0-beta02-unity}
- No platform-specific changes.
#### React Native {#1.3.0-beta02-reactnative}
- No platform-specific changes.
#### Flutter {#1.3.0-beta02-flutter}
- Renamed `tapsell_legacy_adapter` into `tapsell_mediation_legacy` for native ads using `legacy` adapter.

### 1.3.0-beta01 {#1.3.0-beta01}
Published in 2025-10-21
#### Common {#1.3.0-beta01-common}
- Reduce the size of SDK.
- Minor internal improvements.
#### Unity {#1.3.0-beta01-unity}
- No release
#### React Native {#1.3.0-beta01-reactnative}
- No release
#### Flutter {#1.3.0-beta01-flutter}
- No release

## Version 1.2 {#1.2}

### 1.2.0 {#1.2.0}
Published in 2025-10-08
#### Common {#1.2.0-common}
- Added support for [edge-to-edge](https://developer.android.com/develop/ui/views/layout/edge-to-edge) layouts to ensure
  Android `15` compatibility.
- Enhanced image loading functionality by integrating with host-app image loaders (`Coil` and `Glide` currently
  supported).
- Optimized proguard rules for improved R8 fullMode compatibility.
- Various performance improvements and bug fixes.
#### Unity {#1.2.0-unity}
- Updated Unity editor compatibility to `2021.3.45f2`.
- Upgraded **EDM4U** dependency to `1.2.186`.
- Updated Android **target-sdk** to API level `34`.
- Raised Android **min-sdk-version** to API level `24` to resolve Kotlin dexing issues on older
  versions [source](https://stackoverflow.com/a/78401220/2389923).
- Included Unity `*.meta` files in project structure.
- Added Appodeal maven repository support for `Chartboost` adapter integration.
- Removed version prefix (`v`) from package naming convention.
#### React Native {#1.2.0-reactnative}
- No release
#### Flutter {#1.2.0-flutter}
- No release

## Version 1.1 {#1.1}

### 1.1.0 {#1.1.0}
Published in 2025-09-14
#### Common {#1.1.0-common}
- Upgraded Tapsell Legacy adapter to [4.9.13](/docs/sdk-legacy/platforms/android/changelog#4.9.13).
  - Fixed ANR issue in video ads by migrating Android Media Player to `ExoPlayer`.
- Upgraded OkHttp to [4.11.0](https://square.github.io/okhttp/changelogs/changelog_4x/#version-4110).
- Enhanced video player performance for smoother ad playback.
- Optimized network data usage efficiency to reduce bandwidth consumption.
- Improved image-downloader and resource management for better memory usage.
- Improved **CafeBazaar**'s modal launching for a more seamless experience.
- Suppressed **GooglePlayService** outdated notification to avoid distracting users.
- Fixed the issue of **IntentChooser** dialog appearing twice.
- Resolved an issue with back button press handling.
- Utilized the `tpsl` prefix for the obfuscation dictionary to position our classes at the bottom of IDE suggestions &
  improving code navigation.
- Additional internal optimizations and improvements.
#### Unity {#1.1.0-unity}
- Move `TapsellMediationAdmobAdapterSignature` to admob plugin
#### React Native {#1.1.0-reactnative}
- No release
#### Flutter {#1.1.0-flutter}
- Move `TapsellMediationAdmobAdapterSignature` to admob plugin.

## Version 1.0 {#1.0}

### 1.0.2-beta09 {#1.0.2-beta09}
Published in 2025-05-15
#### Common {#1.0.2-beta09-common}
- Upgraded Tapsell Legacy adapter to [4.9.11](/docs/sdk-legacy/platforms/android/changelog#4.9.11)
  - Fixed showing close icon in the interstitial banner when rotation happens.
- Reduce conflict between multiple adapters like `chartboost`, `mintegral`, `pangle` and `google-ads`.
- Additional internal optimizations and improvements.
#### Unity #02 {#1.0.2-beta09-unity-02}
- Fixed startup crash `NoSuchMethodError` by adding missed runtime dependencies: `androidx.core:core-ktx` and
  `androidx.lifecycle:lifecycle-process`.
  The exact error log message:
  ```
  Error JavaBinder *** Uncaught remote exception!  (Exceptions are not yet supported across processes.)
  Error JavaBinder java.lang.NoSuchMethodError: No static method registerReceiver(Landroid/content/Context;Landroid/content/BroadcastReceiver;Landroid/content/IntentFilter;Ljava/lang/String;Landroid/os/Handler;I)Landroid/content/Intent; in class Landroidx/core/content/ContextCompat; or its super classes (declaration of 'androidx.core.content.ContextCompat' appears in /data/app/packagename-8jC3eOK_gulNllIYqZiLiQ==/base.apk)
  ```
#### Unity #01 {#1.0.2-beta09-unity-01}
- Fixed the duplicated keys existence in `gradleTemplate.properties`
#### React Native {#1.0.2-beta09-reactnative}
- No release
#### Flutter {#1.0.2-beta09-flutter}
- No release

### 1.0.2-beta08 {#1.0.2-beta08}
Published in 2025-05-15
#### Common {#1.0.2-beta08-common}
- Added an error message for onFailure callback in RequestResultListener.
- Upgraded Tapsell Legacy adapter version to v4.9.10
- Fix the back navigation issue to avoid side effects on global back navigation.
#### Unity {#1.0.2-beta08-unity}
- Add `message` field for `onFailure` callback in requests.
#### React Native {#1.0.2-beta08-reactnative}
- Add `message` field for `onFailure` callback in requests.
#### Flutter {#1.0.2-beta08-flutter}
- Add `message` field for `onFailure` callback in requests.

### 1.0.2-beta07 {#1.0.2-beta07}
Published in 2025-04-22
#### Common {#1.0.2-beta07-common}
- Downgraded Applovin Admob adapter version to v23.4.0.0 to fix compile issues in Unity 2021
#### Unity {#1.0.2-beta07-unity}
- No release
#### React Native {#1.0.2-beta07-reactnative}
- No release
#### Flutter {#1.0.2-beta07-flutter}
- No release

### 1.0.2-beta06 {#1.0.2-beta06}
Published in 2025-03-10
#### Common {#1.0.2-beta06-common}
- Upgraded Tapsell Legacy adapter version to v4.9.9 to support new back navigation API in Android 13+
- Downgraded Admob version to v23.4.0 to fix compile issues in Unity 2021
#### Unity {#1.0.2-beta06-unity}
- No release
#### React Native {#1.0.2-beta06-reactnative}
- No release
#### Flutter {#1.0.2-beta06-flutter}
- No release

### 1.0.2-beta05 {#1.0.2-beta05}
Published in 2025-03-10
#### Common {#1.0.2-beta05-common}
- Upgraded Tapsell Legacy adapter version to v4.9.8: Fixed conflict with InAppBilling SDKs.
- Upgraded Admob version to v23.6.0
- Upgraded Yandex version to v7.6.0.0.
- Upgraded Applovin version to v13.1.0.
- Upgraded IronSource version to v8.6.1.
- Upgraded Mintegral version to v16.8.61.
- Upgraded Vungle version to v7.4.3.
- Upgraded UnityAds version to v4.13.1.
- Upgraded Fyber (Digital Turbine Exchange) version to v8.3.6.
- Upgraded Wortise version to v1.6.1.
#### Unity {#1.0.2-beta05-unity}
- No release
#### React Native {#1.0.2-beta05-reactnative}
- No release
#### Flutter {#1.0.2-beta05-flutter}
- No release

### 1.0.2-beta04 {#1.0.2-beta04}
Published in 2025-01-26
#### Common {#1.0.2-beta04-common}
- Changed validation of zone id and app key format.
- Improved functionality of the SDK
#### Unity {#1.0.2-beta04-unity}
- No release
#### React Native {#1.0.2-beta04-reactnative}
- Remove `Flipper` from project. It is integrated by ReactNative automatically
#### Flutter {#1.0.2-beta04-flutter}
- No release

### 1.0.2-beta03 {#1.0.2-beta03}
Published in 2024-12-23
#### Common {#1.0.2-beta03-common}
- Added onAdClosed callback for native ads to identify video completion state.
- Upgraded Tapsell Legacy adapter version to v4.9.7-rc01. Migrated from Android MediaPlayer to ExoPlayer
#### Unity {#1.0.2-beta03-unity}
- No release
#### React Native {#1.0.2-beta03-reactnative}
- No release
#### Flutter {#1.0.2-beta03-flutter}
- No release

### 1.0.2-beta02 {#1.0.2-beta02}
Published in 2024-11-13
#### Common {#1.0.2-beta02-common}
- Added support for fully clickable native videos
- Upgraded Tapsell Legacy adapter version to v4.9.4
#### Unity {#1.0.2-beta02-unity}
- No release
#### React Native {#1.0.2-beta02-reactnative}
- No release
#### Flutter {#1.0.2-beta02-flutter}
- No release

### 1.0.2-beta01 {#1.0.2-beta01}
Published in 2024-11-05
#### Common {#1.0.2-beta01-common}
- Added support to play native videos automatically
#### Unity {#1.0.2-beta01-unity}
- No release
#### React Native {#1.0.2-beta01-reactnative}
- No release
#### Flutter {#1.0.2-beta01-flutter}
- Added Native Banner support

### 1.0.1-beta09 {#1.0.1-beta09}
Published in 2024-10-20
#### Common {#1.0.1-beta09-common}
- Fixed issue in playing native video ad
#### Unity {#1.0.1-beta09-unity}
- No release
#### React Native {#1.0.1-beta09-reactnative}
- No release
#### Flutter {#1.0.1-beta09-flutter}
- No release

### 1.0.1-beta08 {#1.0.1-beta08}
Published in 2024-09-30
#### Common {#1.0.1-beta08-common}
- Added Yandex mediation AdNetwork adapter
- Removed developer market from user consent management
- Upgraded targetSdk to version 34 (Android 14)
- Upgraded Admob version to v23.0.0
- Upgraded Applovin adapter to v12.6.0: No need to use applovin sdk key in AndroidManifest.xml.
- Upgraded Tapsell Legacy adapter version to v4.9.3
- Upgraded IronSource adapter version to v8.2.1
- Upgraded UnityAds adapter version to v4.12.2
- Upgraded Mintegral adapter version to v16.8.11
- Upgraded Fyber (Digital Turbine Exchange) adapter version to v8.3.0
- Upgraded Chartboost adapter version to v4.9.1
#### Unity {#1.0.1-beta08-unity}
- No release
#### React Native {#1.0.1-beta08-reactnative}
- No release
#### Flutter {#1.0.1-beta08-flutter}
- Added destroy banner method

### 1.0.1-beta07 {#1.0.1-beta07}
Published in 2024-09-30
#### Common {#1.0.1-beta07-common}
- Improve GDPR user consent flow for GooglePlay users
#### Unity {#1.0.1-beta07-unity}
- Added RTL unity package
#### React Native {#1.0.1-beta07-reactnative}
- No release
#### Flutter {#1.0.1-beta07-flutter}
- No release

### 1.0.1-beta06 {#1.0.1-beta06}
Published in 2024-04-30
#### Common {#1.0.1-beta06-common}
- Added AppOpen ad type. This type is currently supported by Admob, Applovin and Wortise.
- Added Adaptive banner size for all external ad networks.
- Changed user consent approach in Admob adapter. load and show consent form only when required.
- Upgraded Tapsell Legacy adapter version to v4.9.0
- Upgraded Mintegral version to v16.6.71
- Upgraded Wortise adapter version to v1.5.1. Adding pangle maven url is required now.
#### Unity {#1.0.1-beta06-unity}
- Added support for Unity 2020
#### React Native {#1.0.1-beta06-reactnative}
- Initialized plugin
#### Flutter {#1.0.1-beta06-flutter}
- Initialized plugin

### 1.0.1-beta05 {#1.0.1-beta05}
Published in 2024-03-02
#### Common {#1.0.1-beta05-common}
- Fixed PreRoll Media3 crash on Android 6
- Added few Proguard rules
- Upgraded Tapsell Legacy adapter version to v4.9.3. Some proguard rules were updated
- Upgraded Admob version to v22.6.0
- Upgraded Mintegral version to v16.6.34
- Upgraded IronSource version to v7.8.0
- Upgraded Applovin version to v12.2.0
- Upgraded Chartboost version to v4.8.0
- Upgraded UnityAds version to v4.9.2
#### Unity {#1.0.1-beta05-unity}
- No release
#### React Native {#1.0.1-beta05-reactnative}
- No release
#### Flutter {#1.0.1-beta05-flutter}
- No release

### 1.0.1-beta04 {#1.0.1-beta04}
Published in 2024-01-10
#### Common {#1.0.1-beta04-common}
- Improvements on Tapsell.requestMultipleNativeAds() to fix error previous request is still trying ...
#### Unity {#1.0.1-beta04-unity}
- No release
#### React Native {#1.0.1-beta04-reactnative}
- No release
#### Flutter {#1.0.1-beta04-flutter}
- No release

### 1.0.1-beta03 {#1.0.1-beta03}
Published in 2023-12-19
#### Common {#1.0.1-beta03-common}
- Added support for fully clickable native ads
#### Unity {#1.0.1-beta03-unity}
- No release
#### React Native {#1.0.1-beta03-reactnative}
- No release
#### Flutter {#1.0.1-beta03-flutter}
- No release

### 1.0.1-beta02 {#1.0.1-beta02}
Published in 2024-12-11
#### Common {#1.0.1-beta02-common}
- No major Changes
#### Unity {#1.0.1-beta02-unity}
- No release
#### React Native {#1.0.1-beta02-reactnative}
- No release
#### Flutter {#1.0.1-beta02-flutter}
- No release

### 1.0.1-beta01 {#1.0.1-beta01}
Published in 2023-12-11
#### Common {#1.0.1-beta01-common}
- Added pause and resume feature to Native video according to the app lifecycle state. this pauses the video in onPause
  and resume it in onResume
- Fixed showing default video in pre-roll ad.
- Fixed PreRoll UI issues.
#### Unity {#1.0.1-beta01-unity}
- No release
#### React Native {#1.0.1-beta01-reactnative}
- No release
#### Flutter {#1.0.1-beta01-flutter}
- No release

### 1.0.0-beta10 {#1.0.0-beta10}
Published in 2023-10-17
#### Common {#1.0.0-beta10-common}
- Added PreRoll IMA to Mediation with Media3 ExoPlayer.
- Added few GDPR changes. Added developer market key
- Added Gradle 8 proguard R8 rules for Tapsell legacy adapter, Retrofit, OkHttp, IMA and ExoPlayer
- Upgraded Tapsell Legacy adapter version to v4.8.5
#### Unity {#1.0.0-beta10-unity}
- No release
#### React Native {#1.0.0-beta10-reactnative}
- No release
#### Flutter {#1.0.0-beta10-flutter}
- No release

### 1.0.0-beta09 {#1.0.0-beta09}
Published in 2023-09-19
#### Common {#1.0.0-beta09-common}
- Added optional custom parameters for all requests to pas extra info: @Nullable Map requestParams
#### Unity {#1.0.0-beta09-unity}
- No release
#### React Native {#1.0.0-beta09-reactnative}
- No release
#### Flutter {#1.0.0-beta09-flutter}
- No release

### 1.0.0-beta08 {#1.0.0-beta08}
Published in 2023-09-03
#### Common {#1.0.0-beta08-common}
- Added User consent configs for GDPR in Tapsell and all other AdNetwork adapters
- Changed the ad networks initialization state listener. Initialize SDK when at least one of the ad networks is
  initialized successfully.
- Added IronSource AdNetwork adapter.
- Added Vungle(Liftoff) AdNetwork adapter.
#### Unity {#1.0.0-beta08-unity}
- No release
#### React Native {#1.0.0-beta08-reactnative}
- No release
#### Flutter {#1.0.0-beta08-flutter}
- No release

### 1.0.0-beta07 {#1.0.0-beta07}
Published in 2023-08-14
#### Common {#1.0.0-beta07-common}
- Upgraded Tapsell Legacy adapter version to v4.9.4. Removed Collecting user's installed apps due to Google Play policy
  error.
#### Unity {#1.0.0-beta07-unity}
- No release
#### React Native {#1.0.0-beta07-reactnative}
- No release
#### Flutter {#1.0.0-beta07-flutter}
- No release


# سوالات رایج SDK

URL: https://developer.tapsell.ir/fa/docs/sdk/faq/

---

# FAQ

> Source: https://developer.tapsell.ir/docs/sdk/faq

## Common/Android

##### Error `D8: java.lang.NullPointerException: Cannot invoke "String.length()" because "<parameter1>" is null` [^](https://github.com/koral--/android-gif-drawable/issues/869) {#r8-upgrade}

This issue will be occurred when you're using `AGP < 8`. To fix this issue in building, the `R8` dependency must be
upgraded by:

    ```groovy
    // Project-level build.gradle.kts file
    buildscript {
      dependencies {
        // ...
        classpath("com.android.tools:r8:8.1.56")
      }
    }
    ```
    ```groovy
    // Project-level build.gradle file
    buildscript {
      dependencies {
        // ...
        classpath 'com.android.tools:r8:8.1.56'
      }
    }
    ```


# Google Play Data Safety

URL: https://developer.tapsell.ir/fa/docs/sdk/policies/data-safety/

---

# Google Play Data Safety

> Source: https://developer.tapsell.ir/docs/sdk/policies/data-safety

According to [Googleâs data safety](https://support.google.com/googleplay/android-developer/answer/10787469?hl=en#zippy=%2Cdata-types%2Cwhat-users-will-see%2Cdata-collection%2Cpurposes%2Coptional-format-for-sdks%2Cdata-sharing%2Cdata-handling%2Cother-app-and-data-disclosures%2Ccommitted-to-follow-the-families-policy-available-soon-to-applicable-apps%2Cam-i-required-to-provide-a-deletion-mechanism-must-it-be-for-any-and-all-user-data%2Chow-should-i-treat-the-collection-and-use-of-ip-addresses) policy, all developers must declare how they collect and handle user data for the apps they publish on
Google Play, and provide details about how they protect this data through security practices like encryption. This
includes data collected and handled through any third party libraries or SDKs used in their apps, such as Tapsell. In
order to facilitate this process, weâve disclosed the Tapsell data collection below.

> The following disclosures include the data collected by Tapsell SDK only.

### Tapsell Data Types

| Data Type                             | Collected | Shared | Ephemeral | Required | Purpose                                                     |
|:--------------------------------------|:---------:|:------:|:---------:|:--------:|:------------------------------------------------------------|
| **Location** (needs permission grant) |    Yes    |   No   |    No     |    No    | Analytics, advertising <br/>or marketing                    |
| **Personal Info**                     |    No     |   No   |    No     |    No    | Analytics, app functionality, <br/>advertising or marketing |
| **Financial Info**                    |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Health and fitness**                |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Messages**                          |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Photos and videos**                 |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Audio files**                       |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Files and docs**                    |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Calendar**                          |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Contacts**                          |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **App Activity**                      |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **Web browsing**                      |    No     |   No   |    No     |    No    | Not Applicable                                              |
| **App info and performance**          |    Yes    |   No   |    No     |   Yes    | Diagnostics, advertising <br/>or marketing                  |
| **Device or other IDs**               |    Yes    |   No   |    No     |   Yes    | Analytics, app functionality, <br/>advertising or marketing |

### Mediation Ad Networks

You can find more information about each ad networks' data collection disclosure purposes here:

- [Admob](https://developers.google.com/admob/android/privacy/play-data-disclosure)
- [Mintegral](https://www.mintegral.com/en/privacy)
- [Applovin](https://www.applovin.com/array-privacy-policy)
- [Liftoff (Vungle)](https://support.vungle.com/hc/en-us/articles/5035287250331-Google-Play-Data-Safety-Questionnaire-for-Liftoff-Monetize#Data_types)
- [IronSource](https://developers.is.com/ironsource-mobile/general/googles-data-safety-questionnaire-full)
- [Yandex](https://ads.yandex.com/helpcenter/en/dev/android/app-privacy-android)
- [Fyber (Digital Turbine Exchange)](https://www.digitalturbine.com/fyber-services-privacy-statement)
- [UnityAds](https://docs.unity.com/ads/en-us/manual/GoogleDataSafety)
- [Wortise](https://docs.wortise.com/v/en/privacy/google-data-safety)
- [ChartBoost](https://docs.chartboost.com/en/mediation/integrate/android/data-safety-details)


# سیاست حفظ حریم خصوصی

URL: https://developer.tapsell.ir/fa/docs/sdk/policies/privacy-policy/

---

# Tapsell Privacy Policy

> Source: https://developer.tapsell.ir/docs/sdk/policies/privacy-policy

### Last Updated: Jan, 2025

Tapsell (hereinafter Company) is committed to protecting your privacy. This Privacy Policy (Policy) applies to those who visit the Websites and Application owned and operated by the Company, as well as Users of our Products and services offered by us (hereinafter collectively Services) and their End Users. This Privacy Policy describes how the Company collects, uses, shares and secures the personal information you provide. It also describes your choices regarding use, access and correction of your personal information. If you have questions or complaints regarding our privacy policy or practices, please contact us with the contacts below. Is the Company classified as a processor or a controller when processing data under the GDPR? Depending on the circumstances, we may be identified as a processor or controller. Information We May Collect or Receive Upon receiving consent from the user, we may collect data such as device type, country, device language, in-game behavior and purchases, IP address, and Google Play advertising ID. Collected data will be used to display advertisement for games and software which match your profile and history.

### Introduction

To build applications and games, developers need to spend a significant amount of financial resources. For most of these developers, displaying advertisements is a major source of income. A company with its Services provides the necessary tools to earn money and continue supporting and building their products. By integrating our SDK into their games and application, developer show you customized advertisement based on your history, location, and other relevant data.

### How we use your personal information?

We utilize your personal data to show ads on your device via automated decision-making process. We apply AI methods and algorithms based on machine learning, model prediction, and optimization techniques. Ads are displayed to you based on information we collected from you such as the content youâre watching, the app youâre using, your device information, your approximate location. Below we list how we and our service providers use Personal Information for legitimate business purposes more comprehensively and inclusive of our legal basis for such collection and use as required by the laws of certain jurisdictions where we operate including: Providing the functionality of the Services and fulfilling your requests. We use this information to manage our contractual relationship with you, or in some cases, based on your consent or our legitimate interest. Aggregating and/or anonymizing Personal Information. We may aggregate and/or anonymize Personal Information so that it will no longer be considered Personal Information. We do so to generate other data for our use, which we may use and disclose for any purpose. Accomplishing our business purposes. For data analysis, for example, to improve the efficiency of our Services; For audits, to verify that our internal processes function as intended and are compliant with legal, regulatory or contractual requirements; For fraud and security monitoring purposes, for example, to detect and prevent cyber attacks or attempts to commit identity theft; For developing new products and services; For enhancing, improving, or modifying our current products and services and/or assisting our developers with this activity; For identifying usage trends, for example, understanding which parts of our Services are of most interest to users; For determining the effectiveness of our promotional campaigns, so that we can adapt our campaigns to the needs and interests of our users or our publishers and other advertising customers users; and For operating and expanding our business activities, for example, understanding which parts of our Services are of most interest to our users or assisting our developers with this activity within their business so we can focus our and their energies and resources on meeting our users interests; We engage in these activities to manage our contractual relationship with you, to comply with a legal obligation, and/or because we have your consent or legitimate interest.

### Information we may collect from users

When you view ads served by **Tapsell** services or interact with services provided by Tapsellâs business Partners, we and/or our business partners may collect your information. The information we collect through our services may include the following typesï¼

- **OS Information**
    - AndroidId
    - AdvertisingId (More Info)
    - DeviceOS (`MacOs`, `Windows`, `Android`, `iOS`)
    - DevelopmentPlatform
    - OSVersion
    - BuildId(build display id)
- **Application Information**
    - PackageName
    - ApplicationName
    - AppVersionCode
    - AppVersionName
    - AppTargetSdkVersion
    - AppSetId ([More Info](https://developer.android.com/training/articles/app-set-id))
    - AppSetScope ([More Info](https://developer.android.com/training/articles/app-set-id))
    - GDPR Status ([More Info](https://gdpr.eu/))
    - Development Platform (`Android`, `iOS`, `Flutter`, `ReactNative`, â¦)
- **Telephony Information**
    - NetworkType (Only when the developer added runtime permission for *android.permission.READ_PHONE_STATE* and user granted this particular permission)
- **Device Info**
    - DeviceManufacturer
    - DeviceBrand
    - DeviceModel
    - Build Fingerprint
    - LimitAdTrackingStatus
    - TimeZone
    - RootStatus
    - EmulatorStatus
    - Screen Information
        - ScreenDensity
        - ScreenOrientation
        - ScreenHeight
        - ScreenWidth
    - DeviceLanguage
    - MemoryInfo (general information about the memory state of the system)
        - TotalMemory (The total RAM accessible by the kernel)
        - AvailableMemory (The available RAM on the system)
        - LowMemoryStatus (The low memory situation)
    - **BatteryInfo**
        - BatteryPercentage
        - isCharging
    - **Location Information:** Our systems (like most used on the web) automatically collect an IP address when Users access the Service. IP addresses can be used to learn about the general location (e.g.Â city, state and country) of the Users point of access to the web. However, collecting location information in SDK is dependant to userâs grant of location runtime permissions.
    - **Other information:** We may obtain additional information about you from third parties such as marketers, partners, researchers, and others. We may combine information that we collect from you with information about you that we obtain from such third parties and information derived from any other subscription, product, or service we provide. Use of collected data Overview We primarily use the information we collect to provide, administer, operate and improve our Services.

### Why we collect userâs personal information?

Here are some reasons why we are using user personal information to provide our services:

- To enhance user experience and satisfaction, we want to show ads that are relevant and interesting to you. By using your personal information, we can tailor the ads to your preferences, needs, and goals. This way, you can discover new products, services, and opportunities that may benefit you.
- To respect user privacy and security, we want to show ads that are appropriate and safe for you. By using your personal information, we can filter out ads that are offensive, harmful, or misleading. This way, you can avoid unwanted content, risks, and scams that may harm you.
- To support user choice and control, we want to show ads that are transparent and fair for you. By using your personal information, we can provide you with options and feedback on the ads you see. This way, you can adjust your settings, opt out of personalized ads, or report any issues that may concern you.
- To create, administer and troubleshoot accounts, Services we provide;
- To provide technical support and respond to inquiries;
- To prevent fraud, enforce the relevant terms of service or licenses, and protect Users;
- To enable user-to-user communications;
- To link to or combine with other information we have gathered;
- To deliver and target advertising, including personalized ads;
- To inform Users of new products and services, including promotional offers;
- And To contact you with information that we believe will be of interest to you.
- As we believe to be necessary or appropriate:
    - under applicable law, including laws outside your country of residence;
    - to comply with legal process;
    - to respond to requests from public and government authorities, including public and government authorities outside your country of residence;
    - to enforce our terms and conditions;
    - to protect our operations or those of our affiliates;
    - to protect our rights, privacy, safety or property, and/or that of our affiliates, you or others;
    - and (g) to allow us to pursue available remedies or limit the damages that we may sustain.

The sensitive personal information like Location will be collected only when users, grant those particular runtime permissions on their devices.

### Family Policy

According to the [GooglePlay Family Policy](https://support.google.com/googleplay/android-developer/answer/9893335?hl=en), if any of the target audiences for your app is children (especially if youâre developing a game), your appâs content must be appropriate for these type of users. Also, you are not allowed to collect some personal information like Google Advertising ID. However, the third party advertising SDKs need this advertising id to provide and serve personalized ads for users. As a result, in Tapsell SDK all users are treated as 13 or older. So as an application developer if youâre going to publish your app in GooglePlay, you need to express that your app targets audiences with age of 13 or older. Otherwise, your app will be removed from GooglePlay according to this policy. Accordingly, children under 13 are prohibited from using our Services, and You should not use the advertising services if any of your audiences are below 13. We encourage parents and legal guardians to take appropriate measures to prevent children under 13 years of age from looking at this Website without the appropriate permission.

### Sharing information

We may share information we collect with the following parties: **Third-party service providers:** We provide User information to other companies that perform Services on Our behalf. For example, we may employ service providers to process payments; host or store our data; deliver email messages or analyze data. **Safety, security, and legal compliance:** We may share your information: When we have a good faith belief that we are required to disclose the information in response to legal process (e.g., if we receive a court order, search warrant, subpoena, or similar legal process); To satisfy any applicable laws or regulations; If we believe that the Service is being used in the commission of a crime, including to report such criminal activity or to exchange information with other companies and organizations for the purposes of fraud protection and credit risk reduction; When we have a good faith belief that there is a meaningful risk that poses a threat to the safety of you, another person or the public generally; and In order to protect the rights or property of Company, including to enforce the terms and licenses that govern the use of the Services. **Sale or merger:** In the event that we explore or undergo a business transition, such as a merger, acquisition by another company, transfer of control, or sale of all or a portion of its assets, we may share or transfer all of your information, including personal information. We may utilize the data collected from applications to place personalized ads in apps and elsewhere. This usage will permit other parties, such as publishers and attribution companies, to learn about the devices that received, viewed or interacted with ads. **With your consent:** We may share your information for other purposes if (i) you direct us to do so or (ii) you consent to such sharing. **De-identified and/or aggregated information:** Some of the information we collect does not directly identify you or your device. We may modify other information that can identify you or your device to limit or prevent such identification by de-identifying it or aggregating it. We can share de-identified and/or aggregated information with any third party without restriction and use it for any purpose.

### Disclose Personal Information:

We disclose personal information To our affiliates for the purposes described in this Privacy Policy.

**List of our affiliates include:** Google Admob, UnityAds (IronSource), Chartboost, Mintegral, Vungle (Liftoff), AdColony, Applovin

We disclose information to our affiliates, as well as the third-party service providers, to facilitate services they provide to us. These can include providers of services such as website hosting, data analysis, payment processing, order fulfilment, information technology and related infrastructure provision, customer service, email delivery, auditing, and other services To third-party sponsors of sweepstakes, contests, and similar promotions. By using the Services, you may elect to disclose personal information on message boards, chat, profile pages, blogs and other services to which you are able to post information and content (including, without limitation, our Social Media Pages). Please note that any information you post or disclose through these services will become public and may be available to other users and the public. We also use and disclose your personal information as necessary or appropriate, especially when we have a legal obligation or legitimate interest to do so:

- To comply with applicable law and regulations. This can include laws outside your country of residence.
- To cooperate with public and government authorities.
- To respond to a request or to provide information we believe is important These can include authorities outside your country of residence.
- To cooperate with law enforcement. For example, when we respond to law enforcement requests and orders or provide the information we believe is important. For other legal reasons.
- To enforce our terms and conditions or licenses;
- And to protect our rights, privacy, safety or property, and/or that of our affiliates, you or others.

In connection with a sale or business transaction. We have a legitimate interest in disclosing or transferring your personal information to a third party in the event of any reorganization, merger, sale, joint venture, assignment, transfer or other disposition of all or any portion of our business, assets or stock (including in connection with any bankruptcy or similar proceedings) Such third parties may include, for example, an acquiring entity and its advisors.

### Data rights

If your located in one of the [GDPR countries](https://www.gdpradvisor.co.uk/gdpr-countries), you can request access to and rectification or erasure of your personal information by sending an email to `legal@tapsell.ir`. We will make commercially reasonable efforts to address your requests within 72 hours.

### Updates to This Privacy Policy:

We may update this privacy policy whenever it is required. The latest version of the policy will govern our processing of information and is posted at https://developers.tapsell.com/docs/policies/privacy-policy/. For any future update to this policy we will post the revised privacy policy to this page.

### Contact Us:

Tapsell Ltd.

legal@tapsell.ir

No. 23, Anahita, Nelson Mandela Blvd (Jordan), Tehran, Tehran 1917654808, IR


# قوانین و شرایط

URL: https://developer.tapsell.ir/fa/docs/sdk/policies/terms-and-conditions/

---

# Terms And Conditions

> Source: https://developer.tapsell.ir/docs/sdk/policies/terms-and-conditions

## Definitions

#### Tapsell

Tapsell is the commercial name for an in-application mobile advertising system, owned and operated by Pegah Dade Kavan Shiraf Co.

#### Advertiser

Any natural or legal person who displays advertisements within applications that cooperate with Tapsell.

#### Publisher

Any natural or legal person who registers in the Tapsell panel and integrates its codes into their application to display ads and generate revenue.

#### User

Anyone who uses applications that cooperate with Tapsell and engages with their advertisements.

---

## General Rules and Regulations of Tapsell

1. Registration in the Tapsell panel implies full acceptance of all Tapsell rules and conditions.
2. As an Iranian website, Tapsell complies with the laws of the Islamic Republic of Iran, and all regulations regarding cybercrimes and electronic commerce are strictly enforced.
3. Your account registration in the Tapsell panel belongs solely to you, and you are not permitted to share it with others or grant access to third parties.
4. Tapsell reserves the right to change these rules at any time. Updated terms will be published on this page. However, financial rule changes will not affect previously established accounts or balances.
5. Financial and non-financial information of publishers, advertisers, and users is considered confidential. Tapsell guarantees that such information will remain private and will not be disclosed to any individual or organization, except as required by court order.
6. Tapsell may temporarily or permanently suspend its services based on its internal policies.
7. The invalidity or non-enforceability of any part of these terms shall not invalidate the rest of the conditions or obligations outlined herein.
8. Failure to enforce a specific provision of these terms does not waive the right to enforce it at a later date.

---

## Terms of Use for Publishers

1. Displaying advertisements within a publisherâs application is only possible after implementing the Tapsell SDK and applying the necessary modifications as described in the guide section.
2. If a publisher commits fraud in video or banner impressions or otherwise violates user or advertiser rights, Tapsell has the right to permanently suspend the publisher's account. In such cases, all available funds in the account will be forfeited in favor of Tapsell.
3. Based on the performance of publishers and Tapsellâs own business policies, Tapsell may choose to terminate cooperation with any publisher without obligation.
4. Invoices are generated in the panel on the 20th of each month, and revenues are transferred to the publisherâs account on the 28th of the same month. Invoicing is only performed for publishers who have entered their financial information into the Tapsell panel by the end of the 19th of each month. Responsibility for entering incorrect information, including wrong IBAN numbers, lies with the publisher. Revenues under 50,000 Tomans will be settled annually according to Tapsellâs procedures.
5. Due to legal requirements for formal companies, publishers must print two copies of the invoice from the Tapsell panel by the end of the 20th of each month and send them to Tapsell. If registered as an individual, the seller section must include the account holderâs signature and fingerprint. If registered as a legal entity, the seller section must include the company stamp along with the CEOâs or authorized signatoryâs signature.
6. Tapsell assumes no responsibility for products, services, or content within the publishersâ applications. Cooperation with applications for displaying advertisements does not imply endorsement of their contents.
7. All rights related to an advertisement belong exclusively to the advertiser. Neither Tapsell nor the publisher holds any rights beyond those specified in these terms.
8. Termination of Services and Partnership:

  - Both Tapsell and the publisher may unilaterally decide to terminate the partnership based on their respective business strategies.
  - Tapsell can technically stop displaying ads on the publisher's app to terminate the agreement. Similarly, the publisher may also technically stop requesting ad displays from Tapsell.
  - It should be noted that all rights and obligations between both parties remain valid up until the termination date, and final settlement will follow Tapsell's standard procedures.

---

## Terms of Use for Advertisers

1. To display an advertisement, the rights to the application must belong to the rightful owner of the intellectual property.
2. All advertisements must be reviewed and approved by Tapsell before being displayed to users. Tapsell has sole discretion in accepting or rejecting an advertisement.
3. Advertisements that meet any of the following conditions will not be accepted:

  - Are in conflict with current laws of the Islamic Republic of Iran or Tapsell's policies.
  - Contain false or misleading content intended to deceive users.
  - Include frightening or disturbing images.
  - Have low-quality or offensive visuals.
  - Feature invalid or unofficial links (e.g., personal websites).
  - May contain other prohibited content not explicitly listed above.

4. Tapsell has the right to disable or remove an advertisement upon receiving any legal notice from judicial or relevant authorities. Any damages resulting from such actions will be the sole responsibility of the advertiser.
5. Tapsell assumes no liability for the products, services, or content promoted by advertisers. Displaying an advertisement does not constitute endorsement of its content.
6. Advertisers agree that Tapsell may adjust pricing rates at any time based on its internal policies.
7. Advertisers may temporarily pause their campaigns at any time and are not subject to any restrictions regarding the duration or timing of their advertisements.

---

## Privacy Policy

Tapsell guarantees the confidentiality of advertisersâ, publishersâ, and usersâ data, and will not disclose any financial or non-financial information without a court order. Information collected during registration includes:

### Publisher and Advertiser Information:

- Name and Last Name: May represent either an individual or legal entity. This is used solely for identification purposes within the panel and has no other use.
- Email Address: Used for unique identification, verification of registration, and communication. It will not be shared with any third-party companies or organizations.
- Phone Number: Used to facilitate urgent communications.
- IP Address: Temporarily monitored for security purposes and to prevent common web violations. IP addresses are not stored or shared after the monitoring period.

### User Data Collected for Targeted Advertising:

The following anonymized data is collected to enable targeted advertising. These data points cannot be traced back to specific individuals and are not stored:

- Mobile carrier company (e.g., Irancell, Hamrah Aval, Rightel)
- Internet connection type (mobile operator, Wi-Fi, etc.)
- Operating system version
- Approximate geographic location (accurate within ~400 square meters)
- Device manufacturer and model
- Device language
- Screen resolution and pixel density

---

## Documentation Usage, AI & Automated Access

The content published on `developer.tapsell.ir` is provided as technical
reference for integrating Tapsell products.

- **License:** Documentation content may be read, quoted, and referenced for the
  purpose of integrating with Tapsell. Tapsell trademarks and logos remain the
  property of Pegah Dade Kavan Shiraf Co.
- **AI & LLM crawlers:** Automated agents and AI crawlers are permitted to read
  and index this documentation. Machine-readable indexes are published at
  [`/llms.txt`](pathname:///llms.txt) and
  [`/llms-full.txt`](pathname:///llms-full.txt), and the crawl policy is declared
  in [`/robots.txt`](pathname:///robots.txt).
