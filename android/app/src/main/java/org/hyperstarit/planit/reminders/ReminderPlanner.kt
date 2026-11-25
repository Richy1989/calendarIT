package org.hyperstarit.planit.reminders

import java.time.LocalTime
import java.time.ZoneId
import org.hyperstarit.planit.calendar.Occurrence
import org.hyperstarit.planit.calendar.Recurrence
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.TaskData

/** One reminder to ring on this phone. */
data class PlannedReminder(
    /** Stable for the same reminder of the same occurrence (it keeps its alarm across reschedules). */
    val key: String,
    /** When it rings, epoch ms. */
    val at: Long,
    val kind: Kind,
    val accountId: String,
    /** The event (series) or task it is for. */
    val itemId: String,
    val title: String,
    /** When the thing itself begins or is due, epoch ms — for the notification's text. */
    val begins: Long,
    val allDay: Boolean,
    val location: String?,
) {
    enum class Kind { EVENT, TASK }
}

/**
 * Which reminders ring on this phone, and when — by the server's rules (ReminderDispatchJob,
 * ReminderOccurrences, TaskReminders):
 * - Only "notification" reminders (channel WebPush): email reminders are the server's to send.
 * - Timed: minutes before the occurrence's start. All-day: before midnight of its date in the
 *   event's own zone, else its calendar's, else this phone's (the server falls back to the zone of
 *   the calendar's other events, which on a phone is in practice the phone's).
 * - Tasks: open ones only; minutes before the due time, or before 09:00 of a due date, in the
 *   task's zone, else its calendar's, else this phone's.
 */
object ReminderPlanner {
    const val NOTIFICATION = "WebPush"

    fun plan(
        occurrences: List<Occurrence>,
        tasks: List<TaskData>,
        calendars: Map<String, CalendarEntity>,
        from: Long,
        to: Long,
        zone: ZoneId,
    ): List<PlannedReminder> {
        val result = ArrayList<PlannedReminder>()
        for (o in occurrences) {
            val begins = if (o.allDay) {
                Recurrence.dateOf(o.start).atStartOfDay(zoneOf(o.timeZone) ?: zoneOf(calendars[o.calendarId]?.timeZone) ?: zone).toInstant().toEpochMilli()
            } else {
                o.start
            }
            for (r in o.reminders.filter { it.channel == NOTIFICATION }.distinctBy { it.minutesBefore }) {
                val at = begins - r.minutesBefore * 60_000L
                if (at in (from + 1)..to) {
                    result += PlannedReminder(
                        key = "e:${o.seriesId ?: o.eventId}:${o.recurrenceId ?: o.start}:${r.minutesBefore}",
                        at = at, kind = PlannedReminder.Kind.EVENT, accountId = o.accountId, itemId = o.seriesId ?: o.eventId,
                        title = o.title, begins = begins, allDay = o.allDay, location = o.location,
                    )
                }
            }
        }
        for (t in tasks) {
            if (t.status !in setOf("NeedsAction", "InProcess")) continue
            val due = t.due ?: continue
            val begins = if (t.dueAllDay) {
                Recurrence.dateOf(due).atTime(LocalTime.of(9, 0))
                    .atZone(zoneOf(t.timeZone) ?: zoneOf(calendars[t.calendarId]?.timeZone) ?: zone).toInstant().toEpochMilli()
            } else {
                due
            }
            for (r in t.reminders.filter { it.channel == NOTIFICATION }.distinctBy { it.minutesBefore }) {
                val at = begins - r.minutesBefore * 60_000L
                if (at in (from + 1)..to) {
                    result += PlannedReminder(
                        // Keyed by the due as stored: a moved due is a new reminder, as on the server.
                        key = "t:${t.id}:$due:${r.minutesBefore}",
                        at = at, kind = PlannedReminder.Kind.TASK, accountId = t.accountId, itemId = t.id,
                        title = t.title, begins = begins, allDay = t.dueAllDay, location = null,
                    )
                }
            }
        }
        return result.sortedBy { it.at }
    }

    private fun zoneOf(id: String?): ZoneId? = id?.let { runCatching { ZoneId.of(it) }.getOrNull() }

}
