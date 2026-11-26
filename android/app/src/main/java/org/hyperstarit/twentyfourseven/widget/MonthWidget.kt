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
import org.hyperstarit.twentyfourseven.ui.theme.AppearancePrefs
import org.hyperstarit.twentyfourseven.ui.theme.Palette
import org.hyperstarit.twentyfourseven.ui.theme.ThemeChoice
import org.hyperstarit.twentyfourseven.ui.theme.colorScheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.map
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
        fun widgetPrefs(): WidgetPrefs
    }

    /** What the widget draws from: the phone's copy of the calendars, kept live while the widget's session runs. */
    private data class Source(
        val events: List<org.hyperstarit.twentyfourseven.data.db.EventData>,
        val overrides: List<org.hyperstarit.twentyfourseven.data.db.OverrideData>,
        val calendars: Map<String, org.hyperstarit.twentyfourseven.data.db.CalendarEntity>,
        val categories: Map<String, org.hyperstarit.twentyfourseven.data.db.CategoryEntity>,
        val hidden: Set<String>,
    )

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val deps = EntryPointAccessors.fromApplication(context, Deps::class.java)
        val db = deps.db()
        val zone = ZoneId.systemDefault()
        val locale = context.resources.configuration.locales[0]
        val firstDay = WeekFields.of(locale).firstDayOfWeek
        val source = combine(
            db.events().observeLive().map { list -> list.map { it.data } },
            db.events().observeLiveOverrides().map { list -> list.map { it.data } },
            db.calendars().observeAll().map { list -> list.associateBy { it.id } },
            db.categories().observeAll().map { list -> list.associateBy { it.id } },
            deps.prefs().hidden,
        ) { e, o, cal, cat, h -> Source(e, o, cal, cat, h) }
        val initial = source.first()

        provideContent {
            val state = currentState<Preferences>()
            val offset = state[OFFSET] ?: 0
            state[REVISION] // a saved look or theme bumps it, so this draws again with the new one
            val look = deps.widgetPrefs().look(WidgetKind.MONTH)
            val data by source.collectAsState(initial)
            val today = LocalDate.now(zone)
            val month = YearMonth.from(today).plusMonths(offset.toLong())
            val range = MonthModel.range(month, firstDay)
            val byDate = Occurrences.byDate(
                Occurrences.build(data.events, data.overrides, data.calendars, data.categories, data.hidden, range, zone), range, zone,
            )
            val size = LocalSize.current
            val weeks = java.time.temporal.ChronoUnit.DAYS.between(range.from, range.to).toInt() / 7
            val cell = (size.height - HEADER - WEEKDAYS) / weeks
            val perDay = (((cell - DAY_NUMBER - 1.dp) / LINE).toInt()).coerceIn(0, 6)
            val palettes = palettes(context)
            GlanceTheme(colors = themeColors(palettes)) {
                Month(MonthModel.build(month, firstDay, today, byDate, perDay), locale, glass(palettes, look.background), cellStyle(palettes, look))
            }
        }
    }

    @Composable
    private fun Month(grid: MonthGrid, locale: Locale, glass: Glass?, style: CellStyle) {
        val surface = if (glass == null) GlanceModifier.fillMaxSize().background(GlanceTheme.colors.background).cornerRadius(20.dp)
        else GlanceModifier.fillMaxSize().background(glass.fill).cornerRadius(19.dp)
        // Glass: a faint light rim around a see-through fill (Glance has no blur or borders).
        if (glass != null) {
            Box(GlanceModifier.fillMaxSize().background(glass.rim).cornerRadius(20.dp).padding(1.dp)) { MonthBody(grid, locale, surface, style) }
        } else {
            MonthBody(grid, locale, surface, style)
        }
    }

    @Composable
    private fun MonthBody(grid: MonthGrid, locale: Locale, surface: GlanceModifier, style: CellStyle) {
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
            grid.weeks.forEachIndexed { row, week ->
                Row(GlanceModifier.fillMaxWidth().defaultWeight()) {
                    week.forEachIndexed { col, day -> Day(day, row, col, style, GlanceModifier.defaultWeight().fillMaxHeight()) }
                }
            }
        }
    }

    /** One day: a line to its left and above (not on the grid's outer edge), today's fill, the number and its chips. */
    @Composable
    private fun Day(day: DayCell, row: Int, col: Int, style: CellStyle, modifier: GlanceModifier) {
        val open = Intent(Intent.ACTION_VIEW).setClass(androidx.glance.LocalContext.current, MainActivity::class.java)
            .putExtra(MainActivity.EXTRA_DATE, day.date.toString())
            .setData("twentyfourseven://day/${day.date}".toUri()) // one pending intent per day
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        Row(modifier) {
            if (col > 0 && style.border != null) Box(GlanceModifier.width(1.dp).fillMaxHeight().background(style.border)) {}
            Column(
                GlanceModifier.defaultWeight().fillMaxHeight()
                    .let { if (day.today && style.today != null) it.background(style.today) else it }
                    .clickable(actionStartActivity(open)),
            ) {
                if (row > 0 && style.border != null) Box(GlanceModifier.fillMaxWidth().height(1.dp).background(style.border)) {}
                Text(
                    day.date.dayOfMonth.toString(),
                    modifier = GlanceModifier.fillMaxWidth().height(DAY_NUMBER),
                    style = TextStyle(
                        fontSize = 11.sp,
                        textAlign = TextAlign.Center,
                        fontWeight = if (day.today) FontWeight.Bold else FontWeight.Normal,
                        color = if (day.inMonth) GlanceTheme.colors.onSurface else GlanceTheme.colors.outline,
                    ),
                )
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
                    Text("+${day.more}", modifier = GlanceModifier.padding(horizontal = 2.dp),
                        style = TextStyle(fontSize = 9.sp, color = GlanceTheme.colors.onSurfaceVariant))
                }
            }
        }
    }

    companion object {
        val OFFSET = intPreferencesKey("month_offset")
        private val REVISION = intPreferencesKey("look_revision")
        private val HEADER = 36.dp
        private val WEEKDAYS = 16.dp
        private val DAY_NUMBER = 16.dp
        private val LINE = 13.dp
        private val DEFAULT = Palette.CornerStore.accent

        /** The app's theme (Settings → Appearance): one palette, or Daylight and Midnight for "Match phone". */
        private fun palettes(context: Context): Palettes {
            val saved = context.getSharedPreferences("appearance", Context.MODE_PRIVATE).getString("theme", null)
            val choice = ThemeChoice.entries.firstOrNull { it.name == saved } ?: AppearancePrefs.DEFAULT
            return if (choice == ThemeChoice.AUTO) Palettes(Palette.Daylight, Palette.Midnight) else Palettes(choice.palette(true), null)
        }

        private fun themeColors(p: Palettes): androidx.glance.color.ColorProviders =
            if (p.night != null) androidx.glance.material3.ColorProviders(light = p.day.colorScheme(), dark = p.night.colorScheme())
            else androidx.glance.material3.ColorProviders(p.day.colorScheme())

        /** A colour taken from the theme ([pick]), or the same for day and night under "Match phone". */
        private fun Palettes.provider(pick: (Palette) -> Color): ColorProvider =
            if (night != null) androidx.glance.color.ColorProvider(day = pick(day), night = pick(night)) else ColorProvider(pick(day))

        /** A colour the user picked (or the theme's [themed] one) at its opacity; null when fully transparent. */
        private fun Palettes.resolve(c: WidgetColor, themed: (Palette) -> Color): ColorProvider? {
            if (c.alpha <= 0) return null
            val a = c.alpha / 100f
            return provider { p -> (c.rgb?.let { Color(0xFF000000.toInt() or it) } ?: themed(p)).copy(alpha = a) }
        }

        private fun cellStyle(p: Palettes, look: WidgetLook) = CellStyle(
            border = p.resolve(look.border) { it.colorScheme().onSurface },
            today = p.resolve(look.today) { it.colorScheme().primary },
        )

        /** The see-through background and its rim, or null when the widget is opaque (Settings → Widgets). */
        private fun glass(p: Palettes, background: Int): Glass? {
            val opacity = background / 100f
            if (opacity >= 1f) return null
            return Glass(
                p.provider { it.colorScheme().background.copy(alpha = opacity) },
                p.provider { it.colorScheme().onSurface.copy(alpha = 0.18f) },
            )
        }

        private fun onColor(c: Color) = if (0.299 * c.red + 0.587 * c.green + 0.114 * c.blue > 0.6) Color(0xFF1A1A1A) else Color.White

        /**
         * Redraws every month widget (after a sync, a change, a saved look). The revision makes a
         * running widget session read its look and theme again.
         */
        suspend fun refresh(context: Context) = runCatching {
            GlanceAppWidgetManager(context).getGlanceIds(MonthWidget::class.java).forEach { id ->
                updateAppWidgetState(context, id) { it[REVISION] = (it[REVISION] ?: 0) + 1 }
            }
            MonthWidget().updateAll(context)
        }
    }
}

private class Palettes(val day: Palette, val night: Palette?)

private class Glass(val fill: ColorProvider, val rim: ColorProvider)

/** The lines between days and today's fill; null = not drawn. */
private class CellStyle(val border: ColorProvider?, val today: ColorProvider?)

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
