package org.hyperstarit.planit.data.db

import androidx.room.Entity
import androidx.room.PrimaryKey

/** A calendar as the server last sent it. [readOnly] marks a subscription. */
@Entity(tableName = "calendars")
data class CalendarEntity(
    @PrimaryKey val id: String,
    val name: String,
    val color: String?,
    val timeZone: String?,
    val defaultCategoryId: String?,
    val readOnly: Boolean,
    val sortOrder: Int,
    val version: String,
)

@Entity(tableName = "categories")
data class CategoryEntity(
    @PrimaryKey val id: String,
    val name: String,
    val color: String,
    val version: String,
)
