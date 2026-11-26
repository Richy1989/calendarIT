import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.ksp)
    alias(libs.plugins.hilt)
    alias(libs.plugins.room)
}

/**
 * Release signing, from secrets/keystore.properties (local, ignored by git) or RELEASE_* environment
 * variables (CI). Neither present → release builds come out unsigned. See secrets/README.md.
 */
val secretsDir = rootProject.projectDir.parentFile.resolve("secrets")
val keystoreProperties = Properties().apply {
    val file = secretsDir.resolve("keystore.properties")
    if (file.isFile) file.inputStream().use(::load)
}

fun signingValue(env: String, key: String): String? =
    System.getenv(env)?.takeIf { it.isNotBlank() } ?: keystoreProperties.getProperty(key)?.takeIf { it.isNotBlank() }

val releaseStoreFile = signingValue("RELEASE_KEYSTORE_FILE", "storeFile")?.let { secretsDir.resolve(it) }

android {
    namespace = "org.hyperstarit.twentyfourseven"
    // Compose needs the 37.2 platform; minSdk and targetSdk are what decide device behavior.
    compileSdk {
        version = release(37) { minorApiLevel = 2 }
    }

    defaultConfig {
        // Permanent once on Google Play.
        applicationId = "org.hyperstarit.twentyfourseven"
        minSdk = 34
        targetSdk = 37
        versionCode = 5
        versionName = "0.3.2"
        // Android account type for the system calendar: the app id, so debug and release builds
        // installed side by side keep their accounts apart.
        resValue("string", "account_type", "org.hyperstarit.twentyfourseven")
    }

    // foss: no Google libraries (GitHub APK, F-Droid; push via UnifiedPush).
    // play: the Play Store build (adds FCM in a later phase).
    flavorDimensions += "distribution"
    productFlavors {
        create("foss") { dimension = "distribution" }
        create("play") { dimension = "distribution" }
    }

    signingConfigs {
        if (releaseStoreFile != null) {
            create("release") {
                storeFile = releaseStoreFile
                storePassword = signingValue("RELEASE_KEYSTORE_PASSWORD", "storePassword")
                keyAlias = signingValue("RELEASE_KEY_ALIAS", "keyAlias")
                keyPassword = signingValue("RELEASE_KEY_PASSWORD", "keyPassword")
            }
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            resValue("string", "account_type", "org.hyperstarit.twentyfourseven.debug")
            versionNameSuffix = "-debug"
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
        resValues = true
    }

    testOptions {
        unitTests.isIncludeAndroidResources = true
    }

    // Reproducible builds (F-Droid): no dependency metadata blob signed into the APK.
    dependenciesInfo {
        includeInApk = false
        includeInBundle = false
    }
}

room {
    schemaDirectory("$projectDir/schemas")
}

// The migration tests (Robolectric, run on the debug variants) read the exported schemas as assets.
android.sourceSets.getByName("debug").assets.directories.add("$projectDir/schemas")

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.core.splashscreen)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    debugImplementation(libs.compose.ui.tooling)

    implementation(libs.hilt.android)
    ksp(libs.hilt.compiler)
    implementation(libs.androidx.hilt.viewmodel.compose)

    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)

    implementation(libs.work.runtime)
    implementation(libs.androidx.hilt.work)
    ksp(libs.androidx.hilt.compiler)

    implementation(libs.datastore.preferences)
    implementation(libs.coroutines.android)
    implementation(libs.serialization.json)
    implementation(libs.okhttp)
    implementation(libs.ical4j)
    implementation(libs.glance.appwidget)
    implementation(libs.glance.material3)
    implementation(libs.unifiedpush.connector)
    implementation(libs.kizitonwose.calendar)

    testImplementation(libs.junit)
    testImplementation(libs.coroutines.test)
    testImplementation(libs.okhttp.mockwebserver)
    testImplementation(libs.robolectric)
    testImplementation(libs.okhttp.tls)
    testImplementation(libs.room.testing)
    testImplementation(libs.androidx.test.core)
    testImplementation(libs.work.testing)
}
