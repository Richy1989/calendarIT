package org.hyperstarit.twentyfourseven.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp

/**
 * The web app's design tokens (web/src/index.css), one set per theme: a deep canvas, three surface
 * steps, hairlines, three inks, and the spectrum (violet → accent → highlight) spent only on the
 * signature moments — the primary button, today, the selection, the now line.
 */
@Immutable
data class Palette(
    val name: String,
    val isDark: Boolean,
    val bg: Color,
    val bg2: Color,
    val surface: Color,
    val surface2: Color,
    val surface3: Color,
    val hairline: Color,
    val hairlineStrong: Color,
    val text: Color,
    val textDim: Color,
    val textFaint: Color,
    val onAccent: Color,
    val danger: Color,
    val dangerInk: Color,
    val accent: Color,
    val highlight: Color,
    val gradTint: Color,
    val overlay: Color,
    val glow: Float,
) {
    /** The gradient's start: the accent leaning toward the tint (the web mixes 60/40). */
    val violet: Color = lerp(accent, gradTint, 0.4f)

    /** Links and accent-coloured text: the accent pulled toward the ink so it reads on the canvas. */
    val link: Color = lerp(accent, text, 0.28f)

    val grad: Brush get() = Brush.linearGradient(listOf(violet, accent, highlight), start = Offset.Zero, end = Offset.Infinite)

    val gradSoft: Brush
        get() = Brush.linearGradient(
            listOf(violet.copy(alpha = 0.22f), highlight.copy(alpha = 0.18f)), start = Offset.Zero, end = Offset.Infinite,
        )

    /** [color] laid over the surface at [amount], as `color-mix(in srgb, color X%, surface)`. */
    fun tint(color: Color, amount: Float, over: Color = surface): Color = lerp(over, color, amount)

    companion object {
        /** The default: the icon's warm ink, cream type and coral, glowing toward amber. */
        val CornerStore = Palette(
            name = "Corner Store", isDark = true,
            bg = Color(0xFF141110), bg2 = Color(0xFF181412),
            surface = Color(0xFF1F1A17), surface2 = Color(0xFF26201C), surface3 = Color(0xFF2F2823),
            hairline = Color(0xFF372F29), hairlineStrong = Color(0xFF4A3F37),
            text = Color(0xFFFFF0D7), textDim = Color(0xFFC9B9A0), textFaint = Color(0xFF8F8070),
            onAccent = Color(0xFF28221F), danger = Color(0xFFFF4D6D), dangerInk = Color(0xFFFFB3BF),
            accent = Color(0xFFFF694E), highlight = Color(0xFFFFB547), gradTint = Color(0xFFFF3D6E),
            overlay = Color(0xFF0A0807), glow = 0.8f,
        )

        val Midnight = Palette(
            name = "Midnight", isDark = true,
            bg = Color(0xFF0A0D14), bg2 = Color(0xFF0D111A),
            surface = Color(0xFF121723), surface2 = Color(0xFF171D2B), surface3 = Color(0xFF1E2537),
            hairline = Color(0xFF232C3E), hairlineStrong = Color(0xFF313C54),
            text = Color(0xFFE7ECF5), textDim = Color(0xFF9AA4B8), textFaint = Color(0xFF62708A),
            onAccent = Color(0xFF071018), danger = Color(0xFFFF6B6B), dangerInk = Color(0xFFFFB0B0),
            accent = Color(0xFF6E8BFF), highlight = Color(0xFF35E0D4), gradTint = Color(0xFF9B5CFF),
            overlay = Color(0xFF06080D), glow = 1f,
        )

        val Daylight = Palette(
            name = "Daylight", isDark = false,
            bg = Color(0xFFF4F6FB), bg2 = Color(0xFFEEF1F7),
            surface = Color(0xFFFFFFFF), surface2 = Color(0xFFF4F6FA), surface3 = Color(0xFFE8ECF3),
            hairline = Color(0xFFDDE2EC), hairlineStrong = Color(0xFFC5CDDC),
            text = Color(0xFF172033), textDim = Color(0xFF48546B), textFaint = Color(0xFF636F87),
            onAccent = Color(0xFFFFFFFF), danger = Color(0xFFD63A3A), dangerInk = Color(0xFFB42323),
            accent = Color(0xFF4A63E0), highlight = Color(0xFF0B8F86), gradTint = Color(0xFF8A4DFF),
            overlay = Color(0xFF1A2235), glow = 0.55f,
        )

        val Graphite = Palette(
            name = "Graphite", isDark = true,
            bg = Color(0xFF111112), bg2 = Color(0xFF151516),
            surface = Color(0xFF1B1B1D), surface2 = Color(0xFF222225), surface3 = Color(0xFF2B2B2F),
            hairline = Color(0xFF2F2F34), hairlineStrong = Color(0xFF404047),
            text = Color(0xFFEDEDEE), textDim = Color(0xFFA9A9B0), textFaint = Color(0xFF7A7A83),
            onAccent = Color(0xFF1A1206), danger = Color(0xFFFF6B6B), dangerInk = Color(0xFFFFB0B0),
            accent = Color(0xFFF0A45A), highlight = Color(0xFFFF8A6B), gradTint = Color(0xFFFF5C93),
            overlay = Color(0xFF050505), glow = 0.7f,
        )
    }
}
