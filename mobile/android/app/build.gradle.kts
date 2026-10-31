plugins {
    id("com.android.application")
    id("kotlin-android")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "cz.zcloud.deskshare"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = JavaVersion.VERSION_17.toString()
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "cz.zcloud.deskshare"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    // "full" = everything incl. remote control (APK on GitHub/web). "play" = Google Play build without the
    // Accessibility service (Play allows it only for accessibility tools); see src/play/AndroidManifest.xml.
    flavorDimensions += "store"
    productFlavors {
        create("full") { dimension = "store" }
        create("play") { dimension = "store" }
    }

    // Release signing comes from CI secrets (the upload keystore never enters the repo). A release build without
    // them fails instead of silently producing a debug-signed package that Play or users would reject.
    val releaseSigning = listOf(
        "DESKSHARE_ANDROID_KEYSTORE_PATH",
        "DESKSHARE_ANDROID_KEYSTORE_PASSWORD",
        "DESKSHARE_ANDROID_KEY_ALIAS",
        "DESKSHARE_ANDROID_KEY_PASSWORD",
    ).associateWith { System.getenv(it) }
    val missingSigning = releaseSigning.filterValues { it.isNullOrBlank() }.keys
    if (gradle.startParameter.taskNames.any { it.contains("release", ignoreCase = true) } && missingSigning.isNotEmpty()) {
        throw GradleException("Android release signing is not configured. Set: " + missingSigning.joinToString(", "))
    }
    if (missingSigning.isEmpty()) {
        signingConfigs {
            create("release") {
                storeFile = file(releaseSigning.getValue("DESKSHARE_ANDROID_KEYSTORE_PATH")!!)
                storePassword = releaseSigning.getValue("DESKSHARE_ANDROID_KEYSTORE_PASSWORD")
                keyAlias = releaseSigning.getValue("DESKSHARE_ANDROID_KEY_ALIAS")
                keyPassword = releaseSigning.getValue("DESKSHARE_ANDROID_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            if (missingSigning.isEmpty()) signingConfig = signingConfigs.getByName("release")
        }
    }
}

flutter {
    source = "../.."
}
