package org.hyperstarit.twentyfourseven.data.db

import androidx.room.TypeConverter
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json

/** Small lists live in one column as JSON. */
class Converters {
    private val json = Json { ignoreUnknownKeys = true }

    @TypeConverter
    fun remindersToText(value: List<Reminder>): String = json.encodeToString(ListSerializer(Reminder.serializer()), value)

    @TypeConverter
    fun textToReminders(value: String): List<Reminder> = json.decodeFromString(ListSerializer(Reminder.serializer()), value)

    @TypeConverter
    fun attendeesToText(value: List<Attendee>): String = json.encodeToString(ListSerializer(Attendee.serializer()), value)

    @TypeConverter
    fun textToAttendees(value: String): List<Attendee> = json.decodeFromString(ListSerializer(Attendee.serializer()), value)

    @TypeConverter
    fun longsToText(value: List<Long>): String = json.encodeToString(ListSerializer(Long.serializer()), value)

    @TypeConverter
    fun textToLongs(value: String): List<Long> = json.decodeFromString(ListSerializer(Long.serializer()), value)
}
