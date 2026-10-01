import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import java.util.Properties

plugins {
    id("com.android.application")
    kotlin("android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// --- Release signing ----------------------------------------------------------
// CI passes the keystore path as ANDROID_KEYSTORE_FILE and the credentials as
// env vars (GitHub secrets). For local builds, drop a keystore.properties file
// next to this module (storeFile/storePassword/keyAlias/keyPassword/storeType),
// which is ignored by git. Never commit secrets.
val keystoreProperties = Properties()
rootProject.file("keystore.properties").takeIf { it.exists() }?.let { f ->
    f.inputStream().use(keystoreProperties::load)
}

val releaseKeystoreFile = System.getenv("ANDROID_KEYSTORE_FILE")
    ?: keystoreProperties.getProperty("storeFile")

// --- Version -------------------------------------------------------------------
// The version comes from the release tag, not from a number in this file that
// somebody has to remember to bump. It was 2.0.0 for a while, which meant a
// release workflow happily published deltator-v2.1.0-arm64-v8a.apk containing an
// app that identified itself as 2.0.0: the filename said one thing, the manifest
// said another, and neither the filename nor the archive could catch it.
//
// The release workflow passes the tag as -PdeltatorVersion (or DELTATOR_VERSION).
// A plain `assembleDebug` here, and the debug CI, get the fallback below, which
// is the last published version.
val appVersion: String = (findProperty("deltatorVersion") as String?)?.trim()?.takeIf { it.isNotEmpty() }
    ?: System.getenv("DELTATOR_VERSION")?.trim()?.takeIf { it.isNotEmpty() }
    ?: "2.0.0"

// major*10000 + minor*100 + patch, so it grows with the version and stays an
// integer for the whole 1.x and 2.x range. Play only accepts a versionCode
// higher than the last one, so this has to be monotonic: it is, for as long as
// minor and patch each stay below 100.
val appVersionPattern = Regex("""^(\d+)\.(\d+)(?:\.(\d+))?(?:[-+][0-9A-Za-z.\-]+)?$""")
val appVersionMatch = appVersionPattern.matchEntire(appVersion)
    ?: throw GradleException(
        "deltatorVersion must look like 2.1.0 or 2.1.0-rc1, got '$appVersion'. " +
            "The Release workflow derives it from the git tag, so fix the tag " +
            "rather than the value: a release must not ship under a version it was not tagged."
    )
val appVersionMinor: Int = appVersionMatch.groupValues[2].toInt()
val appVersionPatch: Int = appVersionMatch.groupValues[3].ifEmpty { "0" }.toInt()

// Enforced rather than only documented, because staying under 100 is what keeps
// this arithmetic from colliding: 2.0.100 and 2.1.0 would both be 20100, and
// 2.100.0 (21000) sorts below 2.99.99 (29999). In both cases the newer version
// gets a versionCode that is not larger, and Android silently refuses to install
// it over the older one.
if (appVersionMinor >= 100 || appVersionPatch >= 100) {
    throw GradleException(
        "deltatorVersion $appVersion has minor=$appVersionMinor patch=$appVersionPatch. " +
            "versionCode is major*10000 + minor*100 + patch, so minor and patch must " +
            "both stay below 100 or versionCode stops increasing with the version."
    )
}

val appVersionCode: Int =
    appVersionMatch.groupValues[1].toInt() * 10_000 + appVersionMinor * 100 + appVersionPatch

android {
    namespace = "io.deltator"
    compileSdk = 36
    ndkVersion = "29.0.14206865"

    defaultConfig {
        applicationId = "io.deltator"
        minSdk = 24
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersion
    }

    signingConfigs {
        if (!releaseKeystoreFile.isNullOrBlank()) {
            create("release") {
                storeFile = file(releaseKeystoreFile)
                storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
                    ?: keystoreProperties.getProperty("storePassword")
                keyAlias = System.getenv("ANDROID_KEY_ALIAS")
                    ?: keystoreProperties.getProperty("keyAlias")
                keyPassword = System.getenv("ANDROID_KEY_PASSWORD")
                    ?: keystoreProperties.getProperty("keyPassword")
                storeType = System.getenv("ANDROID_KEYSTORE_STORE_TYPE")
                    ?: keystoreProperties.getProperty("storeType") ?: "PKCS12"
            }
        }
    }

    buildTypes {
        release {
            isShrinkResources = true
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
        debug {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    // Per-ABI APKs. libtor/libobfs4proxy only exist for the two ARM ABIs, so each
    // ARM release APK carries only its own native libraries and stays ~22 MB.
    // A universal APK is built alongside them as a fallback for devices whose
    // ABI Gradle does not know about; it ships every ABI and is therefore larger.
    splits {
        abi {
            isEnable = true
            reset()
            include("arm64-v8a", "armeabi-v7a")
            isUniversalApk = true
        }
    }

    // Build hev-socks5-tunnel (tun2socks) with ndk-build
    externalNativeBuild {
        ndkBuild {
            path = file("src/main/cpp/Android.mk")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    // Snowflake pluggable transport (Go gomobile bindings)
    implementation(files("libs/golibs-full.aar"))

    // Compose BOM
    val composeBom = platform("androidx.compose:compose-bom:2026.01.01")
    implementation(composeBom)

    // Compose
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    debugImplementation("androidx.compose.ui:ui-tooling")
    debugImplementation("androidx.compose.ui:ui-test-manifest")

    // Activity Compose
    implementation("androidx.activity:activity-compose:1.12.4")

    // Lifecycle & ViewModel
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.10.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.10.0")

    // Coroutines
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    // Core
    implementation("androidx.core:core-ktx:1.17.0")
}