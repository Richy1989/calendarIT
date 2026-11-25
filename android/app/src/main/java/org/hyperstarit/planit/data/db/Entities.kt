package org.hyperstarit.planit.data.db

import androidx.room.ColumnInfo
import androidx.room.Embedded
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey
import kotlinx.serialization.Serializable

// The device's copy of every account's data. Times are epoch milliseconds, UTC; an all-day date is
// that date's midnight UTC (end = the inclusive last day), as on the server.
//
// Events, edited occurrences and tasks each have two tables of the same shape: the server's copy
// (written only by sync) and the device's pending edits (written by the user, until the server
// has taken them). The *_live views show an edit where there is one — so a pull never overwrites
// what the user just did, and a refused change simply falls back to the server's copy.

object AccountKind {
    const val PLANIT = "planit"
    const val CALDAV = "caldav"
}

@Entity(tableName = "accounts")
data class AccountEntity(
    @PrimaryKey val id: String,
    val kind: String,
    /** The server's address as normalized at sign-in (a directory URL). */
    val server: String,
    val username: String,
    /** SHA-256 of the self-signed certificate the user chose to trust (hex, lowercase); null = the system's CAs. */
    val certificatePin: String? = null,
    /** Where the next pull starts; null = from the beginning. */
    val syncCursor: String? = null,
    val lastSyncAt: Long? = null,
    /** Why the last sync failed, in words for the user; null after a success. */
    val lastError: String? = null,
    /** The server ended the session: nothing is sent until the user signs in again. */
    val needsSignIn: Boolean = false,
    /** The server's certificate no longer matches the pin: nothing is sent until the user looks at it. */
    val certificateChanged: Boolean = false,
    val createdAt: Long,
)

@Entity(tableName = "calendars", indices = [Index("accountId")])
data class CalendarEntity(
    @PrimaryKey val id: String,
    val accountId: String,
    val name: String,
    val color: String?,
    val timeZone: String?,
    val defaultCategoryId: String?,
    val defaultReminders: List<Reminder>,
    val readOnly: Boolean,
    val sortOrder: Int,
    val version: String,
)

@Entity(tableName = "categories", indices = [Index("accountId")])
data class CategoryEntity(
    @PrimaryKey val id: String,
    val accountId: String,
    val name: String,
    val color: String,
    val version: String,
)

@Serializable
data class Reminder(val minutesBefore: Int, val channel: String)

@Serializable
data class Attendee(val email: String, val name: String? = null, val status: String)

/** An event as stored: a one-off, or a repeating series' master (its rule, never expanded). */
data class EventData(
    val id: String,
    val accountId: String,
    val calendarId: String,
    val title: String,
    val description: String?,
    val location: String?,
    val categoryId: String?,
    val color: String?,
    val start: Long,
    val end: Long?,
    val allDay: Boolean,
    val timeZone: String?,
    val recurrence: String?,
    /** Instants the rule produces but doesn't show: deleted occurrences and edited ones. */
    val exDates: List<Long>,
    val reminders: List<Reminder>,
    val attendees: List<Attendee>,
    val invitationStatus: String?,
    val organizerEmail: String?,
    /** The server's version this copy is (or, for an edit, was made on); null = never on the server. */
    val version: String?,
)

@Entity(tableName = "events", primaryKeys = ["id"], indices = [Index("accountId"), Index("calendarId")])
data class EventEntity(@Embedded val data: EventData, @ColumnInfo(name = "localDeleted") val localDeleted: Boolean = false)

@Entity(tableName = "event_edits", primaryKeys = ["id"], indices = [Index("accountId")])
data class EventEdit(@Embedded val data: EventData, @ColumnInfo(name = "localDeleted") val localDeleted: Boolean = false)

/** An edited occurrence: it replaces the one its series' rule puts at [recurrenceId]. */
data class OverrideData(
    val id: String,
    val seriesId: String,
    val recurrenceId: Long,
    val title: String,
    val description: String?,
    val location: String?,
    val categoryId: String?,
    val start: Long,
    val end: Long?,
    val allDay: Boolean,
    val timeZone: String?,
    val reminders: List<Reminder>,
)

@Entity(tableName = "overrides", primaryKeys = ["id"], indices = [Index("seriesId")])
data class OverrideEntity(@Embedded val data: OverrideData, @ColumnInfo(name = "localDeleted") val localDeleted: Boolean = false)

/** An edited series' overrides as the device predicts them (all of them, while its edit is pending). */
@Entity(tableName = "override_edits", primaryKeys = ["id"], indices = [Index("seriesId")])
data class OverrideEdit(@Embedded val data: OverrideData, @ColumnInfo(name = "localDeleted") val localDeleted: Boolean = false)

data class TaskData(
    val id: String,
    val accountId: String,
    val calendarId: String,
    val title: String,
    val notes: String?,
    val due: Long?,
    val dueAllDay: Boolean,
    val timeZone: String?,
    val status: String,
    val completedAt: Long?,
    val priority: String?,
    val parentId: String?,
    val sortOrder: Int,
    val reminders: List<Reminder>,
    val version: String?,
)

@Entity(tableName = "tasks", primaryKeys = ["id"], indices = [Index("accountId"), Index("calendarId")])
data class TaskEntity(@Embedded val data: TaskData, @ColumnInfo(name = "localDeleted") val localDeleted: Boolean = false)

@Entity(tableName = "task_edits", primaryKeys = ["id"], indices = [Index("accountId")])
data class TaskEdit(@Embedded val data: TaskData, @ColumnInfo(name = "localDeleted") val localDeleted: Boolean = false)

/** A change made on the device, waiting to be sent — in [seq] order, per account. */
@Entity(tableName = "outbox", indices = [Index("accountId"), Index("entityId")])
data class OutboxEntity(
    @PrimaryKey(autoGenerate = true) val seq: Long = 0,
    val accountId: String,
    val opId: String,
    /** A protocol op name (event.update, task.done, …). */
    val op: String,
    /** "event" or "task": what [entityId] names. */
    val entityKind: String,
    val entityId: String,
    /** The version the change was made on; null for a create (and anything after one, until it is sent). */
    val baseVersion: String?,
    /** ISO 8601, for occurrence ops. */
    val occurrence: String?,
    /** The op's payload, JSON. */
    val data: String?,
    /** Being sent right now: not merged with new changes. */
    val sending: Boolean = false,
    val attempts: Int = 0,
    val nextAttemptAt: Long = 0,
    val lastError: String? = null,
)

/** Something the user should know about a change of theirs the server didn't take. */
@Entity(tableName = "notices", indices = [Index("accountId")])
data class NoticeEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val accountId: String,
    /** "conflict" (changed elsewhere first), "rejected" (not allowed / not valid), "gone" (deleted elsewhere). */
    val kind: String,
    val entityKind: String,
    val entityId: String,
    val title: String,
    val message: String,
    /** For a conflict: the change as the user made it, to apply again on the server's version. */
    val op: String?,
    val data: String?,
    val createdAt: Long,
)
