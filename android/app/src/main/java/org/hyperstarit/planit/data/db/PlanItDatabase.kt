package org.hyperstarit.planit.data.db

import androidx.room.Database
import androidx.room.RoomDatabase

/**
 * The device's copy of the user's data — the app's source of truth while offline. Schemas are
 * exported to app/schemas: a version bump needs a migration, never a destructive fallback.
 */
@Database(entities = [CalendarEntity::class, CategoryEntity::class], version = 1, exportSchema = true)
abstract class PlanItDatabase : RoomDatabase() {
    abstract fun calendars(): CalendarDao

    abstract fun categories(): CategoryDao
}
