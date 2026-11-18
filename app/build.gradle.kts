plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

val panelBuildIdentity = providers.environmentVariable("PANELOS_BUILD_IDENTITY")
    .orElse("PanelHome/1.0+1")
    .get()
val panelBuildNumber = providers.environmentVariable("PANELOS_BUILD_NUMBER")
    .orElse("1")
    .get()
    .toLongOrNull()
    ?: error("PANELOS_BUILD_NUMBER must be an integer")
val panelProtocolVersion = 1

android {
    namespace = "com.panelos.panelhome"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "com.panelos.panelhome"
        minSdk = 24
        targetSdk = 37
        versionCode = 1
        versionName = "1.0"

        buildConfigField("String", "PANEL_BUILD_IDENTITY", "\"${panelBuildIdentity.replace("\"", "\\\"")}\"")
        buildConfigField("long", "PANEL_BUILD_NUMBER", "${panelBuildNumber}L")
        buildConfigField("int", "PANEL_PROTOCOL_VERSION", panelProtocolVersion.toString())
        manifestPlaceholders["panelBuildIdentity"] = panelBuildIdentity
        manifestPlaceholders["panelBuildNumber"] = panelBuildNumber.toString()
        manifestPlaceholders["panelProtocolVersion"] = panelProtocolVersion.toString()

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            optimization {
                enable = false
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    testOptions {
        unitTests {
            // Robolectric needs the merged resources to render Compose on the JVM.
            isIncludeAndroidResources = true
        }
    }
    sourceSets {
        getByName("test").kotlin.directories.add("src/sharedTest/java")
        getByName("androidTest").kotlin.directories.add("src/sharedTest/java")
    }
}

dependencies {
    // Explicit provider only for Wi-Fi TLS; never registered process-wide.
    implementation(libs.conscrypt.android)
    testImplementation(libs.conscrypt.jvm)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(platform(libs.androidx.compose.bom))
    // The home carousel uses HorizontalPager, which lives in foundation.
    implementation(libs.androidx.compose.foundation)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    debugImplementation(libs.androidx.compose.ui.tooling)

    // Camera preview and frames for scanning the pairing QR; ZXing core decodes them.
    implementation(libs.androidx.camera.core)
    implementation(libs.androidx.camera.camera2)
    implementation(libs.androidx.camera.lifecycle)
    implementation(libs.androidx.camera.view)
    implementation(libs.zxing.core)

    testImplementation(libs.junit)
    // android.jar's org.json is a stub that throws; the real implementation
    // lets the JSONL codec be covered host-side.
    testImplementation(libs.json)
    // Host-side screenshots of Compose (test only; nothing ships in the APK).
    testImplementation(libs.robolectric)
    testImplementation(libs.roborazzi)
    testImplementation(libs.roborazzi.compose)
    testImplementation(platform(libs.androidx.compose.bom))
    testImplementation(libs.androidx.compose.ui.test.junit4)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
    androidTestImplementation(libs.junit)
    androidTestImplementation(libs.androidx.test.core.ktx)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.ext.junit)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
}

// The Android AAR contains Android JNI; host tests need the matching OpenJDK JNI instead.
configurations.matching { it.name.contains("UnitTest") }.configureEach {
    exclude(group = "org.conscrypt", module = "conscrypt-android")
}

tasks.withType<Test>().configureEach {
    // Roborazzi writes the Compose screenshots of ChromeScreenshotTest.
    val recordScreenshots = providers.gradleProperty("panelRecordScreenshots").orElse("false").get()
    systemProperty("roborazzi.test.record", recordScreenshots)
    systemProperty("roborazzi.test.verify", (recordScreenshots != "true").toString())
    providers.gradleProperty("panelScreenshotDir").orNull?.let { systemProperty("panel.screenshot.dir", it) }
    // Conscrypt loads JNI; Java 25 requires explicitly enabling native access in the test worker.
    jvmArgs("--enable-native-access=ALL-UNNAMED")
    maxParallelForks = 1
}
