package org.hyperstarit.planit.data.db

import androidx.room.Dao
import androidx.room.Query
import androidx.room.Transaction
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Dao
interface CalendarDao {
    @Query("SELECT * FROM calendars ORDER BY sortOrder")
    fun observeAll(): Flow<List<CalendarEntity>>

    @Upsert
    suspend fun upsert(calendars: List<CalendarEntity>)

    @Query("DELETE FROM calendars WHERE id NOT IN (:keep)")
    suspend fun deleteAllExcept(keep: List<String>)

    /** The server sends calendars whole: what it doesn't list is gone. */
    @Transaction
    suspend fun replaceAll(calendars: List<CalendarEntity>) {
        deleteAllExcept(calendars.map { it.id })
        upsert(calendars)
    }
}

@Dao
interface CategoryDao {
    @Query("SELECT * FROM categories ORDER BY name")
    fun observeAll(): Flow<List<CategoryEntity>>

    @Upsert
    suspend fun upsert(categories: List<CategoryEntity>)

    @Query("DELETE FROM categories WHERE id NOT IN (:keep)")
    suspend fun deleteAllExcept(keep: List<String>)

    @Transaction
    suspend fun replaceAll(categories: List<CategoryEntity>) {
        deleteAllExcept(categories.map { it.id })
        upsert(categories)
    }
}
