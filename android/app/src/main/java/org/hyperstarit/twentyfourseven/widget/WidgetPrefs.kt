package org.hyperstarit.twentyfourseven.widget

import android.content.Context
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/** The home-screen widgets 24seven offers; each has its own look (Settings → Widgets). */
enum class WidgetKind(val key: String, val title: String, val description: String) {
    MONTH("month", "Month widget", "The whole month on your home screen."),
}

/**
 * A colour picked for a widget: [rgb] (0xRRGGBB) or null for the theme's own, at [alpha] 0–100 %
 * (0 = fully transparent).
 */
data class WidgetColor(val rgb: Int?, val alpha: Int)

/** How a widget looks: background opacity 0–100 %, the lines between day cells, today's cell. */
data class WidgetLook(val background: Int, val border: WidgetColor, val today: WidgetColor) {
    companion object {
        val DEFAULT = WidgetLook(background = 100, border = WidgetColor(null, 15), today = WidgetColor(null, 35))
    }
}

/**
 * Widget looks, in plain SharedPreferences so the widget reads them outside Compose. Keys are
 * `<kind>.<setting>`, so another widget kind gets its own set.
 */
@Singleton
class WidgetPrefs @Inject constructor(@ApplicationContext private val context: Context) {
    private val prefs = context.getSharedPreferences("widgets", Context.MODE_PRIVATE)

    fun look(kind: WidgetKind): WidgetLook {
        val d = WidgetLook.DEFAULT
        val k = kind.key
        return WidgetLook(
            background = prefs.getInt("$k.background", legacyBackground(kind) ?: d.background),
            border = color("$k.border", d.border),
            today = color("$k.today", d.today),
        )
    }

    fun save(kind: WidgetKind, look: WidgetLook) {
        val k = kind.key
        prefs.edit {
            putInt("$k.background", look.background.coerceIn(0, 100))
            putColor("$k.border", look.border)
            putColor("$k.today", look.today)
        }
    }

    private fun color(key: String, default: WidgetColor) = WidgetColor(
        rgb = if (prefs.contains("$key.rgb")) prefs.getInt("$key.rgb", 0) and 0xFFFFFF else default.rgb,
        alpha = prefs.getInt("$key.alpha", default.alpha).coerceIn(0, 100),
    )

    private fun android.content.SharedPreferences.Editor.putColor(key: String, c: WidgetColor) {
        if (c.rgb == null) remove("$key.rgb") else putInt("$key.rgb", c.rgb and 0xFFFFFF)
        putInt("$key.alpha", c.alpha.coerceIn(0, 100))
    }

    /** Versions up to 0.3.1 kept the month widget's opacity with the app's appearance. */
    private fun legacyBackground(kind: WidgetKind): Int? {
        if (kind != WidgetKind.MONTH) return null
        val old = context.getSharedPreferences("appearance", Context.MODE_PRIVATE)
        return if (old.contains("widget_opacity")) old.getInt("widget_opacity", 100) else null
    }
}

/** Redraws every placed widget of [kind] (after its look was saved). */
suspend fun refreshWidgets(context: Context, kind: WidgetKind) = when (kind) {
    WidgetKind.MONTH -> MonthWidget.refresh(context)
}

/** For "Add to home screen": the system's own pin dialog. */
suspend fun pinWidget(context: Context, kind: WidgetKind): Boolean = when (kind) {
    WidgetKind.MONTH -> MonthWidgetPin.request(context)
}
