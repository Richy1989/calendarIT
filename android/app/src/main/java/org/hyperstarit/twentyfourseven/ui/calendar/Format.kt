package org.hyperstarit.twentyfourseven.ui.calendar

import android.content.Context
import android.text.format.DateFormat
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.core.graphics.toColorInt
import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.time.format.TextStyle
import java.time.temporal.WeekFields
import java.util.Locale
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.calendar.Recurrence

/** Times, dates and week starts as this device's settings want them. */
class Formats(context: Context, val zone: ZoneId = ZoneId.systemDefault(), val locale: Locale = Locale.getDefault()) {
    val is24h: Boolean = DateFormat.is24HourFormat(context)
    val firstDayOfWeek: DayOfWeek = WeekFields.of(locale).firstDayOfWeek
    private val time = DateTimeFormatter.ofPattern(if (is24h) "HH:mm" else "h:mm a", locale)
    private val longDate = DateTimeFormatter.ofLocalizedDate(FormatStyle.FULL).withLocale(locale)
    private val month = DateTimeFormatter.ofPattern("LLLL yyyy", locale)
    private val shortDay = DateTimeFormatter.ofPattern("EEE d MMM", locale)

    fun time(millis: Long): String = time.format(Instant.ofEpochMilli(millis).atZone(zone))
    fun timeOf(t: java.time.LocalTime): String = time.format(t)
    fun hour(h: Int): String = if (is24h) "%02d".format(h) else (if (h % 12 == 0) "12" else "${h % 12}") + (if (h < 12) " am" else " pm")
    fun longDate(date: LocalDate): String = longDate.format(date)
    fun monthTitle(date: LocalDate): String = month.format(date).replaceFirstChar { it.titlecase(locale) }
    fun shortDay(date: LocalDate): String = shortDay.format(date)
    fun dayName(day: DayOfWeek): String = day.getDisplayName(TextStyle.SHORT, locale)

    /** "09:00 – 10:30", "All day", or for an all-day span "Mon 1 Dec – Wed 3 Dec". */
    fun span(o: Occurrence): String = when {
        o.allDay && o.end > o.start -> "${shortDay(Recurrence.dateOf(o.start))} – ${shortDay(Recurrence.dateOf(o.end))}"
        o.allDay -> "All day"
        else -> {
            val startDate = Instant.ofEpochMilli(o.start).atZone(zone).toLocalDate()
            val endDate = Instant.ofEpochMilli(maxOf(o.start, o.end - 1)).atZone(zone).toLocalDate()
            if (startDate == endDate) "${time(o.start)} – ${time(o.end)}"
            else "${shortDay(startDate)} ${time(o.start)} – ${shortDay(endDate)} ${time(o.end)}"
        }
    }
}

@Composable
fun rememberFormats(): Formats {
    val context = LocalContext.current
    return remember(context) { Formats(context) }
}

fun parseColor(hex: String?): Color? = hex?.let { runCatching { Color(it.toColorInt()) }.getOrNull() }

/** Text that reads on [background]. */
fun onColor(background: Color): Color =
    if (0.299 * background.red + 0.587 * background.green + 0.114 * background.blue > 0.6) Color(0xFF1A1A1A) else Color.White
