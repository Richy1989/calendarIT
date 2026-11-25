package org.hyperstarit.planit.sync

import androidx.room.withTransaction
import java.time.Clock
import java.util.UUID
import kotlinx.serialization.json.JsonObject
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.EventEdit
import org.hyperstarit.planit.data.db.NoticeEntity
import org.hyperstarit.planit.data.db.OutboxEntity
import org.hyperstarit.planit.data.db.OverrideEdit
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.data.db.TaskData
import org.hyperstarit.planit.data.db.TaskEdit

object EntityKind {
    const val EVENT = "event"
    const val TASK = "task"
}

object Ops {
    const val EVENT_CREATE = "event.create"
    const val EVENT_UPDATE = "event.update"
    const val EVENT_DELETE = "event.delete"
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
    private val db: PlanItDatabase,
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
            db.tasks().putEdit(TaskEdit(current.data.copy(status = status, completedAt = if (done) clock.millis() else null)))
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
    private suspend fun enqueue(accountId: String, op: String, kind: String, entityId: String, data: JsonObject?) {
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
                entityId = entityId, baseVersion = base, occurrence = null, data = data?.toString(),
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
