package org.hyperstarit.twentyfourseven.data.db

import androidx.room.Database
import androidx.room.RoomDatabase
import androidx.room.TypeConverters
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/**
 * The device's copy of every account's data — the app's source of truth while offline. Schemas are
 * exported to app/schemas: a version bump needs a migration, never a destructive fallback.
 */
@Database(
    entities = [
        AccountEntity::class, CalendarEntity::class, CategoryEntity::class,
        EventEntity::class, EventEdit::class, OverrideEntity::class, OverrideEdit::class,
        TaskEntity::class, TaskEdit::class, OutboxEntity::class, NoticeEntity::class,
    ],
    version = 4,
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

    companion object {
        val MIGRATION_3_4 = object : Migration(3, 4) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE calendars ADD COLUMN subscriptionError TEXT")
            }
        }

        /**
         * Version 1 (phase 1) held only copies of the one account's calendars and categories, read
         * from the server and never edited on the device. They gain an account here; the next sync
         * fills them in again, so the old copies make way rather than being guessed an account.
         */
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("DROP TABLE IF EXISTS calendars")
                db.execSQL("DROP TABLE IF EXISTS categories")
                V2_SCHEMA.forEach(db::execSQL)
            }
        }

        /** Every synced row gets the account in its key (see [MIGRATION_2_3_SQL]). */
        val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                MIGRATION_2_3_SQL.forEach(db::execSQL)
            }
        }
    }
}
