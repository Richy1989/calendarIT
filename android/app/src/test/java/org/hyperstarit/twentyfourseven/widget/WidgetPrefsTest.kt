package org.hyperstarit.twentyfourseven.widget

import android.content.Context
import androidx.core.content.edit
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], application = android.app.Application::class)
class WidgetPrefsTest {
    private val context: Context = ApplicationProvider.getApplicationContext()

    @Before
    fun clear() {
        context.getSharedPreferences("widgets", Context.MODE_PRIVATE).edit { clear() }
        context.getSharedPreferences("appearance", Context.MODE_PRIVATE).edit { clear() }
    }

    @Test
    fun freshInstallGetsTheDefault() {
        assertEquals(WidgetLook.DEFAULT, WidgetPrefs(context).look(WidgetKind.MONTH))
    }

    @Test
    fun keepsTheOpacitySetBeforeWidgetSettingsExisted() {
        context.getSharedPreferences("appearance", Context.MODE_PRIVATE).edit { putInt("widget_opacity", 40) }
        assertEquals(40, WidgetPrefs(context).look(WidgetKind.MONTH).background)
    }

    @Test
    fun savedLookComesBackIncludingTransparentLinesAndThemeColour() {
        val prefs = WidgetPrefs(context)
        val look = WidgetLook(background = 0, border = WidgetColor(0xFFFFFF, 0), today = WidgetColor(null, 60))
        prefs.save(WidgetKind.MONTH, look)
        assertEquals(look, WidgetPrefs(context).look(WidgetKind.MONTH))
        // Back to the theme's colour.
        prefs.save(WidgetKind.MONTH, look.copy(border = WidgetColor(null, 20)))
        assertEquals(WidgetColor(null, 20), prefs.look(WidgetKind.MONTH).border)
    }
}
