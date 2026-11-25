package org.hyperstarit.planit.ui.editor

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import org.hyperstarit.planit.calendar.Occurrence
import org.hyperstarit.planit.calendar.Recurrence
import org.hyperstarit.planit.calendar.RepeatRule
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.Reminder

/** Which part of a repeating series a change is for. */
enum class Scope { ONE, FOLLOWING, ALL }

/** What the editor opens on. */
sealed interface EditTarget {
    data class New(val date: LocalDate, val time: LocalTime?) : EditTarget

    /** [scope] is null for a one-off. */
    data class Existing(val occurrence: Occurrence, val scope: Scope?) : EditTarget
}

/** The form. Times are the viewer's wall clock; all-day uses only the dates. */
class EventForm(val zone: ZoneId) {
    var title by mutableStateOf("")
    var location by mutableStateOf("")
    var notes by mutableStateOf("")
    var allDay by mutableStateOf(false)
    var startDate by mutableStateOf(LocalDate.now(zone))
    var startTime by mutableStateOf(LocalTime.of(9, 0))
    var endDate by mutableStateOf(LocalDate.now(zone))
    var endTime by mutableStateOf(LocalTime.of(10, 0))
    var calendarId by mutableStateOf<String?>(null)
    var categoryId by mutableStateOf<String?>(null)
    var repeat by mutableStateOf<RepeatRule?>(null)
    /** A rule the editor can't express: kept as it is unless the user picks another. */
    var keptRule by mutableStateOf<String?>(null)
    var reminders by mutableStateOf<List<Reminder>>(emptyList())
    var timeZone: String? = null

    fun start(): Long = if (allDay) Recurrence.dayMillis(startDate) else startDate.atTime(startTime).atZone(zone).toInstant().toEpochMilli()

    fun end(): Long = if (allDay) Recurrence.dayMillis(endDate) else endDate.atTime(endTime).atZone(zone).toInstant().toEpochMilli()

    fun rule(): String? = repeat?.format(allDay, timeZone?.let { runCatching { ZoneId.of(it) }.getOrNull() } ?: zone) ?: keptRule

    /** Moving the start keeps the length, as calendars do. */
    fun moveStart(date: LocalDate, time: LocalTime) {
        val length = java.time.Duration.between(startDate.atTime(startTime), endDate.atTime(endTime))
        startDate = date
        startTime = time
        val end = date.atTime(time).plus(length)
        endDate = end.toLocalDate()
        endTime = end.toLocalTime()
    }

    fun error(): String? = when {
        title.isBlank() -> "Give it a title."
        calendarId == null -> "Pick a calendar."
        end() < start() -> "It can't end before it starts."
        else -> null
    }

    fun fill(e: EventData, start: Long, end: Long, allDay: Boolean, title: String, location: String?, notes: String?, categoryId: String?, reminders: List<Reminder>) {
        this.title = title
        this.location = location.orEmpty()
        this.notes = notes.orEmpty()
        this.allDay = allDay
        if (allDay) {
            startDate = Recurrence.dateOf(start)
            endDate = Recurrence.dateOf(end).coerceAtLeast(startDate)
        } else {
            val s = Instant.ofEpochMilli(start).atZone(zone)
            val en = Instant.ofEpochMilli(maxOf(start, end)).atZone(zone)
            startDate = s.toLocalDate(); startTime = s.toLocalTime().withSecond(0).withNano(0)
            endDate = en.toLocalDate(); endTime = en.toLocalTime().withSecond(0).withNano(0)
        }
        calendarId = e.calendarId
        this.categoryId = categoryId
        timeZone = e.timeZone
        val seriesZone = e.timeZone?.let { runCatching { ZoneId.of(it) }.getOrNull() } ?: zone
        repeat = RepeatRule.parse(e.recurrence, seriesZone)
        keptRule = if (repeat == null) e.recurrence else null
        this.reminders = reminders
    }

    /** The event as saved: [base] keeps what the form doesn't show (guests, invitation, version). */
    fun toEvent(base: EventData): EventData = base.copy(
        calendarId = calendarId ?: base.calendarId,
        title = title.trim(),
        location = location.trim().ifEmpty { null },
        description = notes.ifBlank { null },
        categoryId = categoryId,
        start = start(),
        end = end(),
        allDay = allDay,
        timeZone = if (allDay) base.timeZone else (base.timeZone ?: zone.id),
        recurrence = rule(),
        reminders = reminders,
    )
}
