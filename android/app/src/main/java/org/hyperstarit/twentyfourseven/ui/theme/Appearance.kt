package org.hyperstarit.twentyfourseven.ui.theme

import android.content.Context
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** The app's look: the web's four themes, or the phone's light/dark setting picking between two. */
enum class ThemeChoice(val label: String) {
    CORNER_STORE("Corner Store"),
    AUTO("Match phone"),
    MIDNIGHT("Midnight"),
    DAYLIGHT("Daylight"),
    GRAPHITE("Graphite"),
    ;

    fun palette(systemDark: Boolean): Palette = when (this) {
        CORNER_STORE -> Palette.CornerStore
        AUTO -> if (systemDark) Palette.Midnight else Palette.Daylight
        MIDNIGHT -> Palette.Midnight
        DAYLIGHT -> Palette.Daylight
        GRAPHITE -> Palette.Graphite
    }
}

/**
 * Device-only appearance, in plain SharedPreferences so the first frame already has it (no flash of
 * the wrong theme), and the widget can read it outside Compose.
 */
@Singleton
class AppearancePrefs @Inject constructor(@ApplicationContext context: Context) {
    private val prefs = context.getSharedPreferences("appearance", Context.MODE_PRIVATE)
    private val _theme = MutableStateFlow(read())
    val theme: StateFlow<ThemeChoice> = _theme

    fun setTheme(choice: ThemeChoice) {
        prefs.edit { putString(THEME, choice.name) }
        _theme.value = choice
    }

    private fun read(): ThemeChoice =
        prefs.getString(THEME, null)?.let { saved -> ThemeChoice.entries.firstOrNull { it.name == saved } } ?: DEFAULT

    companion object {
        /** The look of a fresh install, as on the web. */
        val DEFAULT = ThemeChoice.CORNER_STORE
        private const val THEME = "theme"
    }
}
