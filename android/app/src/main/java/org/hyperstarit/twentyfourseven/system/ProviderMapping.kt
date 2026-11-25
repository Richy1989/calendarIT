package org.hyperstarit.twentyfourseven.system

import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.OverrideData

/**
 * 24seven's events as rows of Android's calendar provider (CalendarContract.Events), and back.
 * Kept free of Android types so it can be tested on the JVM: rows are column → value maps, using
 * the provider's column names.
 *
 * - A series is one row with RRULE, EXDATE (only deleted occurrences) and DURATION (the provider
 *   wants a duration, not an end, for repeating events); each edited occurrence is an exception
 *   row pointing at it (ORIGINAL_SYNC_ID, ORIGINAL_INSTANCE_TIME).
 * - All-day: DTSTART at midnight UTC in zone UTC — as 24seven stores it — and an exclusive end.
 * - No reminders are written: 24seven rings them itself, and a second set would ring twice.
 */
object ProviderMapping {
    const val DAY = 86_400_000L
    private const val HOUR = 3_600_000L
    private val EXDATE_FORMAT = DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss'Z'").withZone(ZoneOffset.UTC)

    /** The row for a series master or one-off. [deletedOnly]: its EXDATE without edited instants. */
    fun eventRow(e: EventData, deletedOnly: List<Long>, color: Int?): Map<String, Any?> {
        val end = endOf(e.start, e.end, e.allDay)
        val row = linkedMapOf<String, Any?>(
            "_sync_id" to e.id,
            "title" to e.title,
            "description" to e.description,
            "eventLocation" to e.location,
            "dtstart" to e.start,
            "allDay" to if (e.allDay) 1 else 0,
            "eventTimezone" to if (e.allDay) "UTC" else (e.timeZone ?: "UTC"),
            "eventColor" to color,
            "hasAlarm" to 0,
        )
        if (e.recurrence != null) {
            row["rrule"] = e.recurrence
            row["exdate"] = deletedOnly.sorted().joinToString(",") { EXDATE_FORMAT.format(Instant.ofEpochMilli(it)) }.ifEmpty { null }
            row["duration"] = duration(end - e.start, e.allDay)
            row["dtend"] = null
        } else {
            row["rrule"] = null
            row["exdate"] = null
            row["duration"] = null
            row["dtend"] = end
        }
        return row
    }

    /** The exception row for an edited occurrence of [series]. */
    fun exceptionRow(o: OverrideData, series: EventData, color: Int?): Map<String, Any?> = linkedMapOf(
        "_sync_id" to "${series.id}@${o.recurrenceId}",
        "original_sync_id" to series.id,
        "originalInstanceTime" to o.recurrenceId,
        "originalAllDay" to if (series.allDay) 1 else 0,
        "title" to o.title,
        "description" to o.description,
        "eventLocation" to o.location,
        "dtstart" to o.start,
        "dtend" to endOf(o.start, o.end, o.allDay),
        "allDay" to if (o.allDay) 1 else 0,
        "eventTimezone" to if (o.allDay) "UTC" else (o.timeZone ?: series.timeZone ?: "UTC"),
        "eventColor" to color,
        "hasAlarm" to 0,
        "rrule" to null,
        "exdate" to null,
        "duration" to null,
    )

    /** A fingerprint of a row's content: unchanged → the row isn't written again. */
    fun fingerprint(row: Map<String, Any?>): String = row.entries.joinToString("|") { "${it.key}=${it.value}" }.hashCode().toUInt().toString(16)

    /** 24seven keeps an all-day end as the last day itself; the provider wants the day after. */
    fun endOf(start: Long, end: Long?, allDay: Boolean): Long = when {
        allDay -> (end ?: start).coerceAtLeast(start) + DAY
        else -> (end ?: (start + HOUR)).coerceAtLeast(start)
    }

    private fun duration(millis: Long, allDay: Boolean): String =
        if (allDay) "P${maxOf(1, millis / DAY)}D" else "P${millis / 1000}S"

    // ------------------------------------------------------------------ back

    /** What another app wrote into a row, in 24seven's terms. */
    data class Incoming(
        val title: String,
        val description: String?,
        val location: String?,
        val start: Long,
        /** 24seven's end: for all-day the last day itself. */
        val end: Long,
        val allDay: Boolean,
        val timeZone: String?,
        val rule: String?,
        val exDates: List<Long>,
    )

    fun incoming(row: Map<String, Any?>): Incoming {
        val allDay = (row["allDay"] as? Number)?.toInt() == 1
        val start = (row["dtstart"] as Number).toLong()
        val rule = (row["rrule"] as? String)?.takeIf { it.isNotBlank() }
        val providerEnd = (row["dtend"] as? Number)?.toLong()
            ?: (row["duration"] as? String)?.let { start + parseDuration(it) }
            ?: (start + if (allDay) DAY else HOUR)
        val end = if (allDay) (providerEnd - DAY).coerceAtLeast(start) else providerEnd.coerceAtLeast(start)
        return Incoming(
            title = (row["title"] as? String).orEmpty().ifBlank { "(No title)" },
            description = (row["description"] as? String)?.ifBlank { null },
            location = (row["eventLocation"] as? String)?.ifBlank { null },
            start = start,
            end = end,
            allDay = allDay,
            timeZone = if (allDay) null else (row["eventTimezone"] as? String),
            rule = rule,
            exDates = parseExDates(row["exdate"] as? String),
        )
    }

    /** RFC 5545 durations as calendar apps write them: P1D, P1W, PT1H30M, P3600S. */
    fun parseDuration(text: String): Long {
        val m = Regex("""^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?(?:(\d+)S)?$""").find(text.trim().uppercase())
            ?: return HOUR
        fun g(i: Int) = m.groupValues[i].toLongOrNull() ?: 0
        return ((g(2) * 7 + g(3)) * 24 * 3600 + g(4) * 3600 + g(5) * 60 + g(6) + g(7)) * 1000
    }

    /** EXDATE lists: "20261224T080000Z,20261231T080000Z", or dates "20261224" (midnight UTC). */
    fun parseExDates(text: String?): List<Long> = text.orEmpty()
        .split(',', ';', '\n')
        .map { it.trim().substringAfter(':') }
        .filter { it.isNotEmpty() }
        .mapNotNull { v ->
            runCatching {
                if (v.length == 8) java.time.LocalDate.parse(v, DateTimeFormatter.BASIC_ISO_DATE).atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli()
                else java.time.LocalDateTime.parse(v.trimEnd('Z'), DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss")).toInstant(ZoneOffset.UTC).toEpochMilli()
            }.getOrNull()
        }
}
