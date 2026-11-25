package org.hyperstarit.planit.sync

import java.time.Instant
import java.time.OffsetDateTime
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.hyperstarit.planit.data.db.Attendee
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.CategoryEntity
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.OverrideData
import org.hyperstarit.planit.data.db.Reminder
import org.hyperstarit.planit.data.db.TaskData
import org.hyperstarit.planit.net.ReminderDto
import org.hyperstarit.planit.net.SyncCalendarDto
import org.hyperstarit.planit.net.SyncCategoryDto
import org.hyperstarit.planit.net.SyncSeriesDto
import org.hyperstarit.planit.net.SyncTaskDto

// Wire (protocol 1) ↔ device rows.

internal fun millis(iso: String): Long = OffsetDateTime.parse(iso).toInstant().toEpochMilli()

internal fun iso(millis: Long): String = Instant.ofEpochMilli(millis).toString()

private fun List<ReminderDto>.rows() = map { Reminder(it.minutesBefore, it.channel) }

internal fun SyncCalendarDto.row(accountId: String, order: Int) = CalendarEntity(
    id = id, accountId = accountId, name = name, color = color, timeZone = timeZone,
    defaultCategoryId = defaultCategoryId, defaultReminders = defaultReminders.rows(),
    readOnly = readOnly, sortOrder = order, version = version,
)

internal fun SyncCategoryDto.row(accountId: String) = CategoryEntity(id, accountId, name, color, version)

internal fun SyncSeriesDto.row(accountId: String) = EventData(
    id = id, accountId = accountId, calendarId = calendarId, title = title, description = description,
    location = location, categoryId = categoryId, color = color, start = millis(start), end = end?.let(::millis),
    allDay = allDay, timeZone = timeZone, recurrence = recurrence, exDates = exDates.map(::millis),
    reminders = reminders.rows(), attendees = attendees.map { Attendee(it.email, it.name, it.status) },
    invitationStatus = invitationStatus, organizerEmail = organizerEmail, version = version,
)

internal fun SyncSeriesDto.overrideRows() = overrides.map {
    OverrideData(
        id = it.id, seriesId = id, recurrenceId = millis(it.recurrenceId), title = it.title,
        description = it.description, location = it.location, categoryId = it.categoryId,
        start = millis(it.start), end = it.end?.let(::millis), allDay = it.allDay, timeZone = it.timeZone,
        reminders = it.reminders.rows(),
    )
}

internal fun SyncTaskDto.row(accountId: String) = TaskData(
    id = id, accountId = accountId, calendarId = calendarId, title = title, notes = notes,
    due = due?.let(::millis), dueAllDay = dueAllDay, timeZone = timeZone, status = status,
    completedAt = completedAt?.let(::millis), priority = priority, parentId = parentId,
    sortOrder = sortOrder, reminders = reminders.rows(), version = version,
)

private fun reminders(list: List<Reminder>) = JsonArray(
    list.map { buildJsonObject { put("minutesBefore", it.minutesBefore); put("channel", it.channel) } },
)

/** An event as the REST endpoint's SaveEventRequest. Guests are sent only on create: the editor
 * for them comes later, and a null list leaves the server's guests as they are. */
internal fun EventData.saveRequest(includeAttendees: Boolean): JsonObject = buildJsonObject {
    put("calendarId", calendarId)
    put("title", title)
    put("description", description)
    put("location", location)
    put("categoryId", categoryId)
    put("start", iso(start))
    put("end", end?.let(::iso))
    put("allDay", allDay)
    put("recurrence", recurrence)
    put("timeZone", timeZone)
    put("reminders", reminders(reminders))
    if (includeAttendees) {
        put("attendees", JsonArray(attendees.map { a -> buildJsonObject { put("email", a.email); put("name", a.name) } }))
    } else {
        put("attendees", JsonNull)
    }
}

/** A task as the REST endpoint's SaveTaskRequest. */
internal fun TaskData.saveRequest(): JsonObject = buildJsonObject {
    put("calendarId", calendarId)
    put("title", title)
    put("notes", notes)
    put("due", due?.let(::iso))
    put("dueAllDay", dueAllDay)
    put("timeZone", timeZone)
    put("priority", priority)
    put("parentId", parentId)
    put("sortOrder", sortOrder)
    put("reminders", reminders(reminders))
}

internal fun doneRequest(done: Boolean) = buildJsonObject { put("done", JsonPrimitive(done)) }
