package org.hyperstarit.twentyfourseven.widget

import android.content.Context
import android.content.Intent
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.core.net.toUri
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.graphics.toColorInt
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import org.hyperstarit.twentyfourseven.ui.theme.colorScheme
import androidx.glance.LocalSize
import androidx.glance.action.ActionParameters
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.state.updateAppWidgetState
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.currentState
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.width
import androidx.glance.state.PreferencesGlanceStateDefinition
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextAlign
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle as DateTextStyle
import java.time.temporal.WeekFields
import java.util.Locale
import kotlinx.coroutines.flow.first
import org.hyperstarit.twentyfourseven.MainActivity
import org.hyperstarit.twentyfourseven.calendar.Occurrences
import org.hyperstarit.twentyfourseven.calendar.CalendarPrefs
import org.hyperstarit.twentyfourseven.data.db.AppDatabase

/**
 * The month at a glance on the home screen: every day of the month with the appointments that fit,
 * ‹ › to page, the month's name back to today, a day to open it in the app. It reads the phone's
 * own copy (works offline) and is redrawn after every sync and change.
 */
class MonthWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact
    override val stateDefinition = PreferencesGlanceStateDefinition

    @EntryPoint
    @InstallIn(SingletonComponent::class)
    interface Deps {
        fun db(): AppDatabase
        fun prefs(): CalendarPrefs
    }

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val deps = EntryPointAccessors.fromApplication(context, Deps::class.java)
        val db = deps.db()
        val zone = ZoneId.systemDefault()
        val locale = context.resources.configuration.locales[0]
        val firstDay = WeekFields.of(locale).firstDayOfWeek
        val events = db.events().observeLive().first().map { it.data }
        val overrides = db.events().observeLiveOverrides().first().map { it.data }
        val calendars = db.calendars().observeAll().first().associateBy { it.id }
        val categories = db.categories().observeAll().first().associateBy { it.id }
        val hidden = deps.prefs().hidden.first()

        provideContent {
            val offset = currentState<Preferences>()[OFFSET] ?: 0
            val today = LocalDate.now(zone)
            val month = YearMonth.from(today).plusMonths(offset.toLong())
            val range = MonthModel.range(month, firstDay)
            val byDate = Occurrences.byDate(Occurrences.build(events, overrides, calendars, categories, hidden, range, zone), range, zone)
            val size = LocalSize.current
            val weeks = range.let { java.time.temporal.ChronoUnit.DAYS.between(it.from, it.to).toInt() / 7 }
            val cell = (size.height - HEADER - WEEKDAYS) / weeks
            val perDay = (((cell - DAY_NUMBER) / LINE).toInt()).coerceIn(0, 6)
            GlanceTheme(colors = themeColors(context)) { Month(MonthModel.build(month, firstDay, today, byDate, perDay), locale, glass(context)) }
        }
    }

    @Composable
    private fun Month(grid: MonthGrid, locale: Locale, glass: Glass?) {
        val surface = if (glass == null) GlanceModifier.fillMaxSize().background(GlanceTheme.colors.background).cornerRadius(20.dp)
        else GlanceModifier.fillMaxSize().background(glass.fill).cornerRadius(19.dp)
        // Glass: a faint light rim around a see-through fill (Glance has no blur or borders).
        if (glass != null) {
            Box(GlanceModifier.fillMaxSize().background(glass.rim).cornerRadius(20.dp).padding(1.dp)) { MonthBody(grid, locale, surface) }
        } else {
            MonthBody(grid, locale, surface)
        }
    }

    @Composable
    private fun MonthBody(grid: MonthGrid, locale: Locale, surface: GlanceModifier) {
        Column(surface.padding(4.dp)) {
            Row(GlanceModifier.fillMaxWidth().height(HEADER), verticalAlignment = Alignment.CenterVertically) {
                Text("‹", modifier = GlanceModifier.width(40.dp).clickable(actionRunCallback<PreviousMonth>()),
                    style = TextStyle(fontSize = 22.sp, color = GlanceTheme.colors.onSurface, textAlign = TextAlign.Center))
                Text(
                    DateTimeFormatter.ofPattern("LLLL yyyy", locale).format(grid.month).replaceFirstChar { it.titlecase(locale) },
                    modifier = GlanceModifier.defaultWeight().clickable(actionRunCallback<ThisMonth>()),
                    style = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.Medium, color = GlanceTheme.colors.onSurface, textAlign = TextAlign.Center),
                )
                Text("›", modifier = GlanceModifier.width(40.dp).clickable(actionRunCallback<NextMonth>()),
                    style = TextStyle(fontSize = 22.sp, color = GlanceTheme.colors.onSurface, textAlign = TextAlign.Center))
            }
            Row(GlanceModifier.fillMaxWidth().height(WEEKDAYS)) {
                grid.weekdays.forEach { d ->
                    Text(d.getDisplayName(DateTextStyle.SHORT, locale), modifier = GlanceModifier.defaultWeight(),
                        style = TextStyle(fontSize = 10.sp, color = GlanceTheme.colors.onSurfaceVariant, textAlign = TextAlign.Center))
                }
            }
            grid.weeks.forEach { week ->
                Row(GlanceModifier.fillMaxWidth().defaultWeight()) {
                    week.forEach { day -> Day(day, GlanceModifier.defaultWeight().fillMaxHeight()) }
                }
            }
        }
    }

    @Composable
    private fun Day(day: DayCell, modifier: GlanceModifier) {
        val open = Intent(Intent.ACTION_VIEW).setClass(androidx.glance.LocalContext.current, MainActivity::class.java)
            .putExtra(MainActivity.EXTRA_DATE, day.date.toString())
            .setData("twentyfourseven://day/${day.date}".toUri()) // one pending intent per day
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        Column(modifier.padding(1.dp).clickable(actionStartActivity(open))) {
            Box(
                GlanceModifier.fillMaxWidth().height(DAY_NUMBER)
                    .let { if (day.today) it.background(GlanceTheme.colors.primary).cornerRadius(7.dp) else it },
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    day.date.dayOfMonth.toString(),
                    style = TextStyle(
                        fontSize = 11.sp,
                        fontWeight = if (day.today) FontWeight.Bold else FontWeight.Normal,
                        color = when {
                            day.today -> GlanceTheme.colors.onPrimary
                            day.inMonth -> GlanceTheme.colors.onSurface
                            else -> GlanceTheme.colors.outline
                        },
                    ),
                )
            }
            day.chips.forEach { chip ->
                val color = chip.color?.let { runCatching { Color(it.toColorInt()) }.getOrNull() } ?: DEFAULT
                Text(
                    chip.title,
                    maxLines = 1,
                    modifier = GlanceModifier.fillMaxWidth().height(LINE).padding(horizontal = 2.dp)
                        .background(ColorProvider(if (chip.allDay) color else color.copy(alpha = 0.25f))).cornerRadius(3.dp),
                    style = TextStyle(fontSize = 9.sp, color = if (chip.allDay) ColorProvider(onColor(color)) else GlanceTheme.colors.onSurface),
                )
            }
            if (day.more > 0) {
                Text("+${day.more}", style = TextStyle(fontSize = 9.sp, color = GlanceTheme.colors.onSurfaceVariant))
            }
        }
    }

    companion object {
        val OFFSET = intPreferencesKey("month_offset")
        private val HEADER = 36.dp
        private val WEEKDAYS = 16.dp
        private val DAY_NUMBER = 16.dp
        private val LINE = 13.dp
        private val DEFAULT = org.hyperstarit.twentyfourseven.ui.theme.Palette.CornerStore.accent

        /** The app's theme (Settings → Appearance); "Match phone" lets the launcher pick Daylight or Midnight. */
        private fun themeColors(context: Context): androidx.glance.color.ColorProviders {
            val saved = context.getSharedPreferences("appearance", Context.MODE_PRIVATE).getString("theme", null)
            val choice = org.hyperstarit.twentyfourseven.ui.theme.ThemeChoice.entries.firstOrNull { it.name == saved } ?: org.hyperstarit.twentyfourseven.ui.theme.AppearancePrefs.DEFAULT
            return if (choice == org.hyperstarit.twentyfourseven.ui.theme.ThemeChoice.AUTO) {
                androidx.glance.material3.ColorProviders(
                    light = org.hyperstarit.twentyfourseven.ui.theme.Palette.Daylight.colorScheme(),
                    dark = org.hyperstarit.twentyfourseven.ui.theme.Palette.Midnight.colorScheme(),
                )
            } else {
                androidx.glance.material3.ColorProviders(choice.palette(true).colorScheme())
            }
        }

        /** The see-through background and its rim, or null when the widget is opaque (Settings → Month widget). */
        private fun glass(context: Context): Glass? {
            val opacity = context.getSharedPreferences("appearance", Context.MODE_PRIVATE)
                .getInt(org.hyperstarit.twentyfourseven.ui.theme.AppearancePrefs.WIDGET_OPACITY, 100) / 100f
            if (opacity >= 1f) return null
            val saved = context.getSharedPreferences("appearance", Context.MODE_PRIVATE).getString("theme", null)
            val choice = org.hyperstarit.twentyfourseven.ui.theme.ThemeChoice.entries.firstOrNull { it.name == saved } ?: org.hyperstarit.twentyfourseven.ui.theme.AppearancePrefs.DEFAULT
            fun fill(p: org.hyperstarit.twentyfourseven.ui.theme.Palette) = p.colorScheme().background.copy(alpha = opacity)
            fun rim(p: org.hyperstarit.twentyfourseven.ui.theme.Palette) = p.colorScheme().onSurface.copy(alpha = 0.18f)
            return if (choice == org.hyperstarit.twentyfourseven.ui.theme.ThemeChoice.AUTO) {
                val day = org.hyperstarit.twentyfourseven.ui.theme.Palette.Daylight
                val night = org.hyperstarit.twentyfourseven.ui.theme.Palette.Midnight
                Glass(
                    androidx.glance.color.ColorProvider(day = fill(day), night = fill(night)),
                    androidx.glance.color.ColorProvider(day = rim(day), night = rim(night)),
                )
            } else {
                val p = choice.palette(true)
                Glass(ColorProvider(fill(p)), ColorProvider(rim(p)))
            }
        }

        private fun onColor(c: Color) = if (0.299 * c.red + 0.587 * c.green + 0.114 * c.blue > 0.6) Color(0xFF1A1A1A) else Color.White

        /** Redraws every month widget (after a sync or a change). */
        suspend fun refresh(context: Context) = runCatching { MonthWidget().updateAll(context) }
    }
}

private class Glass(val fill: ColorProvider, val rim: ColorProvider)

private suspend fun shift(context: Context, id: GlanceId, by: Int?) {
    updateAppWidgetState(context, id) { prefs -> prefs[MonthWidget.OFFSET] = if (by == null) 0 else (prefs[MonthWidget.OFFSET] ?: 0) + by }
    MonthWidget().update(context, id)
}

class PreviousMonth : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) = shift(context, glanceId, -1)
}

class NextMonth : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) = shift(context, glanceId, 1)
}

class ThisMonth : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) = shift(context, glanceId, null)
}

class MonthWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = MonthWidget()
}

/** For "Add to home screen": the system's own pin dialog. */
object MonthWidgetPin {
    suspend fun request(context: Context): Boolean =
        GlanceAppWidgetManager(context).requestPinGlanceAppWidget(MonthWidgetReceiver::class.java)
}
