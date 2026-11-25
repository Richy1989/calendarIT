package org.hyperstarit.twentyfourseven.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import org.hyperstarit.twentyfourseven.R

/** The three faces of the web app: Space Grotesk for display, Inter for reading, JetBrains Mono for data. */
private fun variable(res: Int, vararg weights: Int) = FontFamily(
    weights.map { w -> Font(res, FontWeight(w), variationSettings = FontVariation.Settings(FontVariation.weight(w))) },
)

val Display = variable(R.font.space_grotesk, 400, 500, 600, 700)
val Body = variable(R.font.inter, 400, 500, 600, 700)
val Mono = variable(R.font.jetbrains_mono, 400, 500, 600)

private val LocalPalette = staticCompositionLocalOf { Palette.CornerStore }

/** Access to the brand tokens beyond what Material's ColorScheme holds. */
object Brand {
    val colors: Palette
        @Composable @ReadOnlyComposable get() = LocalPalette.current

    /** The web's mono labels: small, uppercase, widely tracked (`.fc-col-header-cell-cushion`). */
    val label = TextStyle(fontFamily = Mono, fontSize = 10.5.sp, fontWeight = FontWeight.Medium, letterSpacing = 0.14.em)

    /** Times and numbers that line up in a column. */
    val data = TextStyle(fontFamily = Mono, fontSize = 12.sp, fontFeatureSettings = "tnum")
}

private fun typography(): Typography {
    val base = Typography()
    fun TextStyle.body() = copy(fontFamily = Body)
    fun TextStyle.display(tracking: Float = -0.015f) = copy(fontFamily = Display, fontWeight = FontWeight.SemiBold, letterSpacing = tracking.em)
    return Typography(
        displayLarge = base.displayLarge.display(-0.02f),
        displayMedium = base.displayMedium.display(-0.02f),
        displaySmall = base.displaySmall.display(-0.02f),
        headlineLarge = base.headlineLarge.display(),
        headlineMedium = base.headlineMedium.display(),
        headlineSmall = base.headlineSmall.display(),
        titleLarge = base.titleLarge.display(),
        titleMedium = base.titleMedium.copy(fontFamily = Body, fontWeight = FontWeight.SemiBold),
        titleSmall = base.titleSmall.copy(fontFamily = Body, fontWeight = FontWeight.SemiBold),
        bodyLarge = base.bodyLarge.body(),
        bodyMedium = base.bodyMedium.body(),
        bodySmall = base.bodySmall.body(),
        labelLarge = base.labelLarge.copy(fontFamily = Body, fontWeight = FontWeight.SemiBold),
        labelMedium = base.labelMedium.body(),
        labelSmall = base.labelSmall.body(),
    )
}

private val shapes = Shapes(
    extraSmall = RoundedCornerShape(8.dp),
    small = RoundedCornerShape(10.dp),
    medium = RoundedCornerShape(12.dp),
    large = RoundedCornerShape(18.dp),
    extraLarge = RoundedCornerShape(24.dp),
)

/** Material's roles filled from the tokens, so stock components (pickers, dialogs, menus) match. */
fun Palette.colorScheme(): ColorScheme {
    val base = if (isDark) darkColorScheme() else lightColorScheme()
    return base.copy(
        primary = accent,
        onPrimary = onAccent,
        primaryContainer = tint(accent, 0.22f),
        onPrimaryContainer = text,
        inversePrimary = highlight,
        secondary = highlight,
        onSecondary = onAccent,
        secondaryContainer = tint(violet, 0.24f),
        onSecondaryContainer = text,
        tertiary = violet,
        onTertiary = onAccent,
        tertiaryContainer = tint(highlight, 0.2f),
        onTertiaryContainer = text,
        background = bg,
        onBackground = text,
        surface = surface,
        onSurface = text,
        surfaceVariant = surface2,
        onSurfaceVariant = textDim,
        surfaceTint = accent,
        inverseSurface = text,
        inverseOnSurface = bg,
        error = danger,
        onError = onAccent,
        errorContainer = tint(danger, 0.16f),
        onErrorContainer = dangerInk,
        outline = hairlineStrong,
        outlineVariant = hairline,
        scrim = overlay,
        surfaceBright = surface3,
        surfaceDim = bg,
        surfaceContainerLowest = bg,
        surfaceContainerLow = bg2,
        surfaceContainer = surface,
        surfaceContainerHigh = surface2,
        surfaceContainerHighest = surface3,
    )
}

/** The app's theme: the chosen palette, its Material mapping, the web's type and radii. */
@Composable
fun AppTheme(palette: Palette = Palette.CornerStore, content: @Composable () -> Unit) {
    CompositionLocalProvider(LocalPalette provides palette) {
        MaterialTheme(colorScheme = palette.colorScheme(), typography = typography(), shapes = shapes, content = content)
    }
}

/** Text on a category color: dark ink on light colors, white on dark ones. */
fun Palette.onColor(color: androidx.compose.ui.graphics.Color) =
    if (0.299 * color.red + 0.587 * color.green + 0.114 * color.blue > 0.6) lerp(bg, androidx.compose.ui.graphics.Color.Black, 0.3f)
    else androidx.compose.ui.graphics.Color.White
