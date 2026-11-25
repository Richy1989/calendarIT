package org.hyperstarit.twentyfourseven.sync

import androidx.room.withTransaction
import java.time.Clock
import java.util.UUID
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.JsonObject
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.EventEdit
import org.hyperstarit.twentyfourseven.data.db.NoticeEntity
import org.hyperstarit.twentyfourseven.data.db.OutboxEntity
import org.hyperstarit.twentyfourseven.data.db.OverrideEdit
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.TaskData
import org.hyperstarit.twentyfourseven.data.db.TaskEdit

object EntityKind {
    const val EVENT = "event"
    const val TASK = "task"
}

object Ops {
    const val EVENT_CREATE = "event.create"
    const val EVENT_UPDATE = "event.update"
    const val EVENT_DELETE = "event.delete"
    const val OCCURRENCE_UPSERT = "event.occurrence.upsert"
    const val OCCURRENCE_DELETE = "event.occurrence.delete"
    const val FOLLOWING_UPDATE = "event.following.update"
    const val FOLLOWING_DELETE = "event.following.delete"
    const val EVENT_RSVP = "event.rsvp"
    const val TASK_CREATE = "task.create"
    const val TASK_UPDATE = "task.update"
    const val TASK_DONE = "task.done"
    const val TASK_DELETE = "task.delete"

    val creates = setOf(EVENT_CREATE, TASK_CREATE)
    val deletes = setOf(EVENT_DELETE, TASK_DELETE)
}

/**
 * Changes the user makes on the device. Each one is shown at once (a pending edit over the server's
 * copy) and queued for the server; [onQueued] then asks for a sync. Offline, they wait.
 *
 * Queued changes to one thing are folded together where the result is the same: edits into its
 * pending create or edit, a delete of something never sent cancels it. What can't be folded is
 * sent in order on the version the user saw — the server takes a run of them as one.
 */
class LocalChanges(
    private val db: AppDatabase,
    private val clock: Clock,
    private val onQueued: () -> Unit,
) {
    // ---------------------------------------------------------------- events

    suspend fun createEvent(event: EventData): String {
        val id = event.id.ifEmpty { UUID.randomUUID().toString() }
        val row = event.copy(id = id, version = null)
        db.withTransaction {
            db.events().putEdit(EventEdit(row))
            enqueue(row.accountId, Ops.EVENT_CREATE, EntityKind.EVENT, id, row.saveRequest(includeAttendees = true))
        }
        onQueued()
        return id
    }

    /**
     * A whole-series (or one-off) edit; occurrence edits come with the editor (phase 4). As on the
     * server, a series that moves (its rule, start, all-day-ness or zone) loses its deleted and
     * edited occurrences: they no longer fall on real instants.
     */
    suspend fun updateEvent(event: EventData) {
        db.withTransaction {
            val current = db.events().live(event.id)?.data ?: return@withTransaction
            val moved = event.recurrence != current.recurrence || event.start != current.start ||
                event.allDay != current.allDay || (!event.allDay && event.timeZone != current.timeZone)
            val firstEdit = db.events().edit(event.id) == null
            db.events().putEdit(EventEdit(event.copy(version = current.version, exDates = if (moved) emptyList() else current.exDates)))
            when {
                moved || event.recurrence == null -> {
                    db.events().deleteOverrideEdits(event.id)
                    // An empty prediction must still hide the server's overrides: mark them gone.
                    db.events().putOverrideEdits(db.events().serverOverrides(event.id).map { OverrideEdit(it.data, localDeleted = true) })
                }
                // The series' edited occurrences stay as they are while the edit waits.
                firstEdit -> db.events().putOverrideEdits(db.events().serverOverrides(event.id).map { OverrideEdit(it.data) })
            }
            enqueue(event.accountId, Ops.EVENT_UPDATE, EntityKind.EVENT, event.id, event.saveRequest(includeAttendees = false))
        }
        onQueued()
    }

    suspend fun deleteEvent(id: String) {
        db.withTransaction {
            val current = db.events().live(id) ?: return@withTransaction
            db.events().putEdit(EventEdit(current.data, localDeleted = true))
            enqueue(current.data.accountId, Ops.EVENT_DELETE, EntityKind.EVENT, id, null)
        }
        onQueued()
    }

    // ---------------------------------------------------------------- one occurrence, and the ones after

    /** The series as the user sees it now: its pending edit (with its predicted overrides) or the server's copy. */
    private suspend fun series(seriesId: String): SeriesEdits.Series? {
        val event = db.events().live(seriesId)?.data ?: return null
        val overrides = if (db.events().edit(seriesId) != null) {
            db.events().overrideEdits(seriesId).filter { !it.localDeleted }.map { it.data }
        } else {
            db.events().serverOverrides(seriesId).map { it.data }
        }
        return SeriesEdits.Series(event, overrides)
    }

    private suspend fun putSeries(s: SeriesEdits.Series) {
        db.events().putEdit(EventEdit(s.event))
        db.events().deleteOverrideEdits(s.event.id)
        db.events().putOverrideEdits(s.overrides.map { OverrideEdit(it) })
    }

    /** "Only this event": [edited] holds the occurrence's new details (rule, calendar and guests stay the series'). */
    suspend fun editOccurrence(seriesId: String, recurrenceId: Long, edited: EventData) {
        db.withTransaction {
            val s = series(seriesId) ?: return@withTransaction
            val existing = s.overrides.firstOrNull { SeriesEdits.truncate(it.recurrenceId) == SeriesEdits.truncate(recurrenceId) }
            val row = org.hyperstarit.twentyfourseven.data.db.OverrideData(
                id = existing?.id ?: "local:$seriesId@$recurrenceId", seriesId = seriesId, recurrenceId = recurrenceId,
                title = edited.title, description = edited.description, location = edited.location, categoryId = edited.categoryId,
                start = edited.start, end = edited.end, allDay = edited.allDay, timeZone = edited.timeZone ?: s.event.timeZone,
                reminders = edited.reminders,
            )
            putSeries(SeriesEdits.upsertOccurrence(s, recurrenceId, row))
            enqueue(s.event.accountId, Ops.OCCURRENCE_UPSERT, EntityKind.EVENT, seriesId, edited.saveRequest(includeAttendees = false), iso(recurrenceId))
        }
        onQueued()
    }

    suspend fun deleteOccurrence(seriesId: String, recurrenceId: Long) {
        db.withTransaction {
            val s = series(seriesId) ?: return@withTransaction
            putSeries(SeriesEdits.deleteOccurrence(s, recurrenceId))
            enqueue(s.event.accountId, Ops.OCCURRENCE_DELETE, EntityKind.EVENT, seriesId, null, iso(recurrenceId))
        }
        onQueued()
    }

    /**
     * "This and following": the series ends before the occurrence and [next] starts a new one there
     * (under a new id). On the first occurrence that is a change of the whole series.
     */
    suspend fun editFollowing(seriesId: String, recurrenceId: Long, next: EventData): String {
        var newId = seriesId
        db.withTransaction {
            val s = series(seriesId) ?: return@withTransaction
            val id = UUID.randomUUID().toString()
            val split = SeriesEdits.split(s, recurrenceId, next.copy(id = id, accountId = s.event.accountId))
            if (split == null) {
                newId = seriesId
                return@withTransaction
            }
            val (kept, started) = split
            putSeries(kept)
            putSeries(started)
            val data = kotlinx.serialization.json.JsonObject(next.saveRequest(includeAttendees = false) + ("id" to kotlinx.serialization.json.JsonPrimitive(id)))
            enqueue(s.event.accountId, Ops.FOLLOWING_UPDATE, EntityKind.EVENT, seriesId, data, iso(recurrenceId))
            newId = id
        }
        if (newId == seriesId) updateEvent(next.copy(id = seriesId)) else onQueued()
        return newId
    }

    suspend fun deleteFollowing(seriesId: String, recurrenceId: Long) {
        var whole = false
        db.withTransaction {
            val s = series(seriesId) ?: return@withTransaction
            val kept = SeriesEdits.deleteFollowing(s, recurrenceId)
            if (kept == null) {
                whole = true
                return@withTransaction
            }
            putSeries(kept)
            enqueue(s.event.accountId, Ops.FOLLOWING_DELETE, EntityKind.EVENT, seriesId, null, iso(recurrenceId))
        }
        if (whole) deleteEvent(seriesId) else onQueued()
    }

    /** Answers an invitation: "Accepted", "Tentative" or "Declined". */
    suspend fun respond(eventId: String, status: String) {
        db.withTransaction {
            val s = series(eventId) ?: return@withTransaction
            putSeries(s.copy(event = s.event.copy(invitationStatus = status)))
            enqueue(
                s.event.accountId, Ops.EVENT_RSVP, EntityKind.EVENT, eventId,
                kotlinx.serialization.json.buildJsonObject { put("status", kotlinx.serialization.json.JsonPrimitive(status)) },
            )
        }
        onQueued()
    }

    // ---------------------------------------------------------------- tasks

    suspend fun createTask(task: TaskData): String {
        val id = task.id.ifEmpty { UUID.randomUUID().toString() }
        val row = task.copy(id = id, version = null)
        db.withTransaction {
            db.tasks().putEdit(TaskEdit(row))
            enqueue(row.accountId, Ops.TASK_CREATE, EntityKind.TASK, id, row.saveRequest())
        }
        onQueued()
        return id
    }

    suspend fun updateTask(task: TaskData) {
        db.withTransaction {
            val current = db.tasks().live(task.id) ?: return@withTransaction
            db.tasks().putEdit(TaskEdit(task.copy(version = current.data.version)))
            enqueue(task.accountId, Ops.TASK_UPDATE, EntityKind.TASK, task.id, task.saveRequest())
        }
        onQueued()
    }

    suspend fun setTaskDone(id: String, done: Boolean) {
        db.withTransaction {
            val current = db.tasks().live(id) ?: return@withTransaction
            val status = if (done) "Completed" else "NeedsAction"
            val now = clock.millis()
            db.tasks().putEdit(TaskEdit(current.data.copy(status = status, completedAt = if (done) now else null)))
            // As on the server: ticking a task off ticks off what is left of its checklist. (These
            // predictions have no change of their own queued: the next pull brings the server's.)
            if (done) {
                db.tasks().observeLive().first().map { it.data }
                    .filter { it.parentId == id && it.status in setOf("NeedsAction", "InProcess") }
                    .forEach { db.tasks().putEdit(TaskEdit(it.copy(status = "Completed", completedAt = now))) }
            }
            enqueue(current.data.accountId, Ops.TASK_DONE, EntityKind.TASK, id, doneRequest(done))
        }
        onQueued()
    }

    suspend fun deleteTask(id: String) {
        db.withTransaction {
            val current = db.tasks().live(id) ?: return@withTransaction
            db.tasks().putEdit(TaskEdit(current.data, localDeleted = true))
            enqueue(current.data.accountId, Ops.TASK_DELETE, EntityKind.TASK, id, null)
        }
        onQueued()
    }

    // ---------------------------------------------------------------- notices

    /** "Apply mine anyway": the change the server refused as a conflict, again, on its version now. */
    suspend fun reapply(noticeId: Long) {
        db.withTransaction {
            val notice = db.notices().get(noticeId) ?: return@withTransaction
            val op = notice.op ?: return@withTransaction
            val data = notice.data?.let { kotlinx.serialization.json.Json.parseToJsonElement(it) as JsonObject }
            val exists = when (notice.entityKind) {
                EntityKind.EVENT -> db.events().server(notice.entityId) != null
                else -> db.tasks().server(notice.entityId) != null
            }
            if (exists) enqueue(notice.accountId, op, notice.entityKind, notice.entityId, data)
            db.notices().delete(noticeId)
        }
        onQueued()
    }

    suspend fun dismiss(noticeId: Long) = db.notices().delete(noticeId)

    // ---------------------------------------------------------------- the queue

    /** Must run inside a transaction. */
    private suspend fun enqueue(accountId: String, op: String, kind: String, entityId: String, data: JsonObject?, occurrence: String? = null) {
        val queued = db.outbox().forEntity(entityId)
        val waiting = queued.filter { !it.sending }
        val last = waiting.lastOrNull()

        if (last != null) {
            val lastIsCreate = last.op in Ops.creates
            val isUpdate = op == Ops.EVENT_UPDATE || op == Ops.TASK_UPDATE
            when {
                // An edit of something not sent yet: it is created as edited.
                isUpdate && lastIsCreate -> {
                    db.outbox().update(listOf(last.copy(data = data.toString())))
                    return
                }
                // Two whole edits in a row: the later one is the whole truth.
                isUpdate && last.op == op -> {
                    db.outbox().update(listOf(last.copy(data = data.toString())))
                    return
                }
                // Deleting what the server never saw: nothing to tell it.
                op in Ops.deletes && waiting.first().op in Ops.creates && queued.none { it.sending } -> {
                    db.outbox().delete(waiting.map { it.seq })
                    dropEdit(kind, entityId)
                    return
                }
            }
        }

        // The version the change is made on: the one the first queued change for it was made on
        // (the server takes a run of changes on one base), else the server's copy's.
        val base = when {
            op in Ops.creates -> null
            queued.isNotEmpty() -> queued.first().baseVersion
            kind == EntityKind.EVENT -> db.events().server(entityId)?.data?.version
            else -> db.tasks().server(entityId)?.data?.version
        }
        db.outbox().insert(
            OutboxEntity(
                accountId = accountId, opId = UUID.randomUUID().toString(), op = op, entityKind = kind,
                entityId = entityId, baseVersion = base, occurrence = occurrence, data = data?.toString(),
            ),
        )
    }

    /** A thing created and deleted before the server saw it leaves no trace. */
    private suspend fun dropEdit(kind: String, entityId: String) {
        if (kind == EntityKind.EVENT) {
            db.events().deleteEdit(entityId)
            db.events().deleteOverrideEdits(entityId)
        } else {
            db.tasks().deleteEdit(entityId)
        }
    }

    internal fun notice(
        accountId: String, kind: String, entityKind: String, entityId: String, title: String, message: String,
        op: String? = null, data: String? = null,
    ) = NoticeEntity(
        accountId = accountId, kind = kind, entityKind = entityKind, entityId = entityId, title = title,
        message = message, op = op, data = data, createdAt = clock.millis(),
    )
}
