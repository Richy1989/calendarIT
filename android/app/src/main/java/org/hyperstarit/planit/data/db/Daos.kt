package org.hyperstarit.planit.data.db

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import androidx.room.Update
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Dao
interface AccountDao {
    @Query("SELECT * FROM accounts ORDER BY createdAt")
    fun observeAll(): Flow<List<AccountEntity>>

    @Query("SELECT * FROM accounts ORDER BY createdAt")
    suspend fun all(): List<AccountEntity>

    @Query("SELECT * FROM accounts WHERE id = :id")
    suspend fun get(id: String): AccountEntity?

    @Query("SELECT * FROM accounts WHERE kind = :kind AND server = :server AND username = :username COLLATE NOCASE")
    suspend fun find(kind: String, server: String, username: String): AccountEntity?

    @Insert
    suspend fun insert(account: AccountEntity)

    @Update
    suspend fun update(account: AccountEntity)

    @Query("DELETE FROM accounts WHERE id = :id")
    suspend fun delete(id: String)

    @Query("UPDATE accounts SET syncCursor = :cursor, lastSyncAt = :at, lastError = NULL WHERE id = :id")
    suspend fun setSynced(id: String, cursor: String, at: Long)

    @Query("UPDATE accounts SET lastError = :error WHERE id = :id")
    suspend fun setError(id: String, error: String?)

    @Query("UPDATE accounts SET certificateChanged = 1, lastError = :error WHERE id = :id")
    suspend fun setCertificateChanged(id: String, error: String)
}

@Dao
interface CalendarDao {
    @Query("SELECT * FROM calendars ORDER BY accountId, sortOrder")
    fun observeAll(): Flow<List<CalendarEntity>>

    @Query("SELECT * FROM calendars WHERE accountId = :accountId ORDER BY sortOrder")
    suspend fun forAccount(accountId: String): List<CalendarEntity>

    @Upsert
    suspend fun upsert(calendars: List<CalendarEntity>)

    @Query("DELETE FROM calendars WHERE accountId = :accountId AND id NOT IN (:keep)")
    suspend fun deleteOthers(accountId: String, keep: List<String>)

    @Query("DELETE FROM calendars WHERE accountId = :accountId")
    suspend fun deleteAccount(accountId: String)
}

@Dao
interface CategoryDao {
    @Query("SELECT * FROM categories ORDER BY name")
    fun observeAll(): Flow<List<CategoryEntity>>

    @Upsert
    suspend fun upsert(categories: List<CategoryEntity>)

    @Query("DELETE FROM categories WHERE accountId = :accountId AND id NOT IN (:keep)")
    suspend fun deleteOthers(accountId: String, keep: List<String>)

    @Query("DELETE FROM categories WHERE accountId = :accountId")
    suspend fun deleteAccount(accountId: String)
}

@Dao
interface EventDao {
    /** Every event as the user sees it: the server's copy, or the pending edit where there is one. */
    @Query(
        "SELECT * FROM events WHERE id NOT IN (SELECT id FROM event_edits) " +
            "UNION ALL SELECT * FROM event_edits WHERE localDeleted = 0",
    )
    fun observeLive(): Flow<List<EventEntity>>

    @Query(
        "SELECT * FROM events WHERE id = :id AND id NOT IN (SELECT id FROM event_edits) " +
            "UNION ALL SELECT * FROM event_edits WHERE id = :id AND localDeleted = 0",
    )
    suspend fun live(id: String): EventEntity?

    /** Edited occurrences as the user sees them: an edited series brings all of its own. */
    @Query(
        "SELECT * FROM overrides WHERE seriesId NOT IN (SELECT id FROM event_edits) " +
            "UNION ALL SELECT * FROM override_edits WHERE localDeleted = 0",
    )
    fun observeLiveOverrides(): Flow<List<OverrideEntity>>

    @Query("SELECT * FROM overrides WHERE seriesId = :seriesId")
    suspend fun serverOverrides(seriesId: String): List<OverrideEntity>

    @Query("SELECT * FROM events WHERE id = :id")
    suspend fun server(id: String): EventEntity?

    @Query("SELECT COUNT(*) FROM events WHERE calendarId = :calendarId")
    suspend fun countInCalendar(calendarId: String): Int

    @Query("SELECT calendarId AS calendarId, COUNT(*) AS count FROM events GROUP BY calendarId")
    fun observeCounts(): Flow<List<CalendarCount>>

    @Upsert
    suspend fun upsert(events: List<EventEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertOverrides(overrides: List<OverrideEntity>)

    @Query("DELETE FROM overrides WHERE seriesId IN (:seriesIds)")
    suspend fun deleteOverridesOf(seriesIds: List<String>)

    @Query("DELETE FROM events WHERE id IN (:ids)")
    suspend fun deleteServer(ids: List<String>)

    @Query("SELECT id FROM events WHERE accountId = :accountId")
    suspend fun serverIds(accountId: String): List<String>

    @Query("SELECT id FROM events WHERE calendarId NOT IN (SELECT id FROM calendars) AND accountId = :accountId")
    suspend fun orphans(accountId: String): List<String>

    @Query("DELETE FROM events WHERE accountId = :accountId")
    suspend fun deleteAccount(accountId: String)

    @Query("DELETE FROM overrides WHERE seriesId NOT IN (SELECT id FROM events)")
    suspend fun deleteOrphanOverrides()

    // ---- pending edits

    @Query("SELECT * FROM event_edits WHERE id = :id")
    suspend fun edit(id: String): EventEdit?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun putEdit(edit: EventEdit)

    @Query("SELECT * FROM override_edits WHERE seriesId = :seriesId")
    suspend fun overrideEdits(seriesId: String): List<OverrideEdit>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun putOverrideEdits(edits: List<OverrideEdit>)

    @Query("DELETE FROM override_edits WHERE seriesId = :seriesId")
    suspend fun deleteOverrideEdits(seriesId: String)

    @Query("DELETE FROM event_edits WHERE id = :id")
    suspend fun deleteEdit(id: String)

    /** Edits nothing is waiting to send any more: the server's copy (pulled since) takes over. */
    @Query("DELETE FROM event_edits WHERE id NOT IN (SELECT entityId FROM outbox WHERE entityKind = 'event')")
    suspend fun dropSettledEdits()

    @Query("DELETE FROM override_edits WHERE seriesId NOT IN (SELECT id FROM event_edits)")
    suspend fun dropSettledOverrideEdits()

    @Query("DELETE FROM event_edits WHERE accountId = :accountId")
    suspend fun deleteAccountEdits(accountId: String)
}

data class CalendarCount(val calendarId: String, val count: Int)

@Dao
interface TaskDao {
    @Query(
        "SELECT * FROM tasks WHERE id NOT IN (SELECT id FROM task_edits) " +
            "UNION ALL SELECT * FROM task_edits WHERE localDeleted = 0",
    )
    fun observeLive(): Flow<List<TaskEntity>>

    @Query(
        "SELECT * FROM tasks WHERE id = :id AND id NOT IN (SELECT id FROM task_edits) " +
            "UNION ALL SELECT * FROM task_edits WHERE id = :id AND localDeleted = 0",
    )
    suspend fun live(id: String): TaskEntity?

    @Query("SELECT * FROM tasks WHERE id = :id")
    suspend fun server(id: String): TaskEntity?

    @Upsert
    suspend fun upsert(tasks: List<TaskEntity>)

    @Query("DELETE FROM tasks WHERE id IN (:ids)")
    suspend fun deleteServer(ids: List<String>)

    @Query("SELECT id FROM tasks WHERE accountId = :accountId")
    suspend fun serverIds(accountId: String): List<String>

    @Query("SELECT id FROM tasks WHERE calendarId NOT IN (SELECT id FROM calendars) AND accountId = :accountId")
    suspend fun orphans(accountId: String): List<String>

    @Query("DELETE FROM tasks WHERE accountId = :accountId")
    suspend fun deleteAccount(accountId: String)

    @Query("SELECT * FROM task_edits WHERE id = :id")
    suspend fun edit(id: String): TaskEdit?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun putEdit(edit: TaskEdit)

    @Query("DELETE FROM task_edits WHERE id = :id")
    suspend fun deleteEdit(id: String)

    @Query("DELETE FROM task_edits WHERE id NOT IN (SELECT entityId FROM outbox WHERE entityKind = 'task')")
    suspend fun dropSettledEdits()

    @Query("DELETE FROM task_edits WHERE accountId = :accountId")
    suspend fun deleteAccountEdits(accountId: String)
}

@Dao
interface OutboxDao {
    @Query("SELECT * FROM outbox WHERE accountId = :accountId ORDER BY seq")
    suspend fun forAccount(accountId: String): List<OutboxEntity>

    @Query("SELECT * FROM outbox WHERE entityId = :entityId ORDER BY seq")
    suspend fun forEntity(entityId: String): List<OutboxEntity>

    @Query("SELECT COUNT(*) FROM outbox")
    fun observePending(): Flow<Int>

    @Insert
    suspend fun insert(op: OutboxEntity): Long

    @Update
    suspend fun update(ops: List<OutboxEntity>)

    @Query("DELETE FROM outbox WHERE seq IN (:seqs)")
    suspend fun delete(seqs: List<Long>)

    @Query("UPDATE outbox SET sending = 0")
    suspend fun clearSending()

    @Query("DELETE FROM outbox WHERE accountId = :accountId")
    suspend fun deleteAccount(accountId: String)
}

@Dao
interface NoticeDao {
    @Query("SELECT * FROM notices ORDER BY createdAt DESC")
    fun observeAll(): Flow<List<NoticeEntity>>

    @Query("SELECT * FROM notices WHERE id = :id")
    suspend fun get(id: Long): NoticeEntity?

    @Insert
    suspend fun insert(notice: NoticeEntity)

    @Query("DELETE FROM notices WHERE id = :id")
    suspend fun delete(id: Long)

    @Query("DELETE FROM notices WHERE accountId = :accountId")
    suspend fun deleteAccount(accountId: String)
}

/** Everything of one account goes with it. */
@Dao
abstract class AccountDataDao {
    @Transaction
    open suspend fun deleteAll(
        accountId: String,
        accounts: AccountDao,
        calendars: CalendarDao,
        categories: CategoryDao,
        events: EventDao,
        tasks: TaskDao,
        outbox: OutboxDao,
        notices: NoticeDao,
    ) {
        outbox.deleteAccount(accountId)
        notices.deleteAccount(accountId)
        events.deleteAccountEdits(accountId)
        events.dropSettledOverrideEdits()
        tasks.deleteAccountEdits(accountId)
        events.deleteAccount(accountId)
        events.deleteOrphanOverrides()
        tasks.deleteAccount(accountId)
        categories.deleteAccount(accountId)
        calendars.deleteAccount(accountId)
        accounts.delete(accountId)
    }
}
