package org.hyperstarit.twentyfourseven.data.db

import androidx.room.Database
import androidx.room.RoomDatabase
import androidx.room.TypeConverters

/**
 * The device's copy of every account's data — the app's source of truth while offline. Schemas are
 * exported to app/schemas: a version bump needs a migration, never a destructive fallback.
 */
@Database(
    entities = [
        AccountEntity::class, CalendarEntity::class, CategoryEntity::class,
        EventEntity::class, EventEdit::class, OverrideEntity::class, OverrideEdit::class,
        TaskEntity::class, TaskEdit::class, OutboxEntity::class, NoticeEntity::class,
        DavAccountEntity::class, DavCalendarEntity::class, DavResourceEntity::class,
    ],
    version = 1,
    exportSchema = true,
)
@TypeConverters(Converters::class)
abstract class AppDatabase : RoomDatabase() {
    abstract fun accounts(): AccountDao
    abstract fun calendars(): CalendarDao
    abstract fun categories(): CategoryDao
    abstract fun events(): EventDao
    abstract fun tasks(): TaskDao
    abstract fun outbox(): OutboxDao
    abstract fun notices(): NoticeDao
    abstract fun accountData(): AccountDataDao
    abstract fun dav(): DavDao
}
