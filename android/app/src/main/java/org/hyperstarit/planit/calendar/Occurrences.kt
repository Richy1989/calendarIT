package org.hyperstarit.planit.calendar

import java.time.LocalDate
import java.time.ZoneId
import org.hyperstarit.planit.data.db.Attendee
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.CategoryEntity
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.OverrideData
import org.hyperstarit.planit.data.db.Reminder

/** One appointment as a view shows it: a one-off, one occurrence of a series, or an edited occurrence. */
data class Occurrence(
    /** Unique among everything shown: the event id, plus the occurrence's original start for a series. */
    val key: String,
    val eventId: String,
    /** The series this is an occurrence of (also for an edited one); null for a one-off. */
    val seriesId: String?,
    /** The occurrence's start in its series' rule; null for a one-off. */
    val recurrenceId: Long?,
    val accountId: String,
    val calendarId: String,
    val calendarName: String,
    val title: String,
    val location: String?,
    val description: String?,
    /** Timed: instants. All-day: the first and the last (inclusive) date's midnight UTC — dates, not instants. */
    val start: Long,
    val end: Long,
    val allDay: Boolean,
    val timeZone: String?,
    /** #RRGGBB, or null for the theme's default. */
    val color: String?,
    val recurrence: String?,
    val reminders: List<Reminder>,
    val attendees: List<Attendee>,
    val invitationStatus: String?,
    val organizerEmail: String?,
    val readOnly: Boolean,
) {
    /** The dates it covers in [zone] (an all-day one: its own dates, wherever it is seen). */
    fun dates(zone: ZoneId): List<LocalDate> {
        val first: LocalDate
        val last: LocalDate
        if (allDay) {
            first = Recurrence.dateOf(start)
            last = Recurrence.dateOf(end).coerceAtLeast(first)
        } else {
            first = java.time.Instant.ofEpochMilli(start).atZone(zone).toLocalDate()
            // Ending exactly at midnight doesn't reach into the next day.
            val endMoment = java.time.Instant.ofEpochMilli(maxOf(start, end - 1)).atZone(zone).toLocalDate()
            last = endMoment.coerceAtLeast(first)
        }
        return generateSequence(first) { it.plusDays(1) }.takeWhile { !it.isAfter(last) }.take(MAX_DAYS).toList()
    }

    private companion object {
        const val MAX_DAYS = 366
    }
}

/** The dates [from, to) as the viewer's calendar shows them. */
data class DateRange(val from: LocalDate, val to: LocalDate)

/**
 * Builds what a range of dates shows, from the stored events (series unexpanded) and edited
 * occurrences — the same rules as the server's range query: timed events by the instants of the
 * viewer's days, all-day ones by their dates.
 */
object Occurrences {
    /** At most this many per build, as the server caps a range response. */
    const val MAX = 20_000
    private const val MAX_PER_SERIES = 3_000
    private const val HOUR = 3_600_000L
    private const val DAY = 86_400_000L

    fun build(
        events: List<EventData>,
        overrides: List<OverrideData>,
        calendars: Map<String, CalendarEntity>,
        categories: Map<String, CategoryEntity>,
        hiddenCalendars: Set<String>,
        range: DateRange,
        zone: ZoneId,
    ): List<Occurrence> {
        val from = range.from.atStartOfDay(zone).toInstant().toEpochMilli()
        val to = range.to.atStartOfDay(zone).toInstant().toEpochMilli()
        val fromDay = Recurrence.dayMillis(range.from)
        val toDay = Recurrence.dayMillis(range.to)
        val visible = events.filter { it.calendarId !in hiddenCalendars && it.calendarId in calendars }
        val seriesById = visible.filter { it.recurrence != null }.associateBy { it.id }
        val result = ArrayList<Occurrence>()

        fun color(event: EventData, categoryId: String?): String? {
            val calendar = calendars[event.calendarId]
            return categoryId?.let { categories[it]?.color }
                ?: (if (categoryId == null) calendar?.defaultCategoryId?.let { categories[it]?.color } else null)
                ?: event.color
                ?: calendar?.color
        }

        fun add(event: EventData, key: String, start: Long, end: Long, seriesId: String?, recurrenceId: Long?, o: OverrideData? = null) {
            if (result.size >= MAX) return
            val calendar = calendars[event.calendarId]
            result += Occurrence(
                key = key, eventId = o?.id ?: event.id, seriesId = seriesId, recurrenceId = recurrenceId,
                accountId = event.accountId, calendarId = event.calendarId, calendarName = calendar?.name.orEmpty(),
                title = o?.title ?: event.title, location = o?.location ?: event.location,
                description = o?.description ?: event.description, start = start, end = end,
                allDay = o?.allDay ?: event.allDay, timeZone = o?.timeZone ?: event.timeZone,
                color = color(event, if (o != null) o.categoryId else event.categoryId), recurrence = event.recurrence,
                reminders = o?.reminders ?: event.reminders, attendees = event.attendees,
                invitationStatus = event.invitationStatus, organizerEmail = event.organizerEmail,
                readOnly = calendar?.readOnly ?: true,
            )
        }

        fun inRange(start: Long, end: Long?, allDay: Boolean): Boolean = if (allDay) {
            // The stored end is the last day's midnight: the event runs to that day's end.
            start < toDay && (end ?: start) > fromDay - DAY
        } else {
            start < to && (end ?: start) >= from
        }

        for (event in visible) {
            if (event.recurrence == null) {
                if (inRange(event.start, event.end, event.allDay)) {
                    add(event, event.id, event.start, event.end ?: event.start, null, null)
                }
                continue
            }
            val end = event.end ?: (event.start + HOUR)
            val spans = Recurrence.expand(
                event.start, end, event.timeZone, event.recurrence, event.exDates,
                if (event.allDay) fromDay else from, if (event.allDay) toDay else to, event.allDay,
            )
            for (span in spans.take(MAX_PER_SERIES)) {
                add(event, "${event.id}@${span.start}", span.start, span.end, event.id, span.start)
            }
        }
        for (o in overrides) {
            val series = seriesById[o.seriesId] ?: continue
            if (inRange(o.start, o.end, o.allDay)) {
                add(series, "${series.id}@${o.recurrenceId}", o.start, o.end ?: o.start, series.id, o.recurrenceId, o)
            }
        }
        return result.sortedWith(compareBy({ !it.allDay }, { it.start }, { it.title }))
    }

    /** Each date of [range] with what it shows; a multi-day event on every day it covers. */
    fun byDate(occurrences: List<Occurrence>, range: DateRange, zone: ZoneId): Map<LocalDate, List<Occurrence>> {
        val map = HashMap<LocalDate, MutableList<Occurrence>>()
        for (o in occurrences) {
            for (date in o.dates(zone)) {
                if (date.isBefore(range.from) || !date.isBefore(range.to)) continue
                map.getOrPut(date) { ArrayList() } += o
            }
        }
        return map
    }
}
