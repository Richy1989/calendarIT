// The app's plugins, declared once here and applied in app/. AGP 9 compiles Kotlin itself; the
// Compose and serialization compiler plugins pin the Kotlin version it uses.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
    alias(libs.plugins.ksp) apply false
    alias(libs.plugins.hilt) apply false
    alias(libs.plugins.room) apply false
}
