package org.hyperstarit.twentyfourseven.calendar

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.ZonedDateTime
import net.fortuna.ical4j.model.Recur

/**
 * Expands a repeat rule into occurrences on the device — the same way the server does
 * (`RecurrenceExpander`); testdata/recurrence-vectors.json holds both to it.
 *
 * - A timed series repeats on its own zone's clock (so 09:00 stays 09:00 across DST); no zone, or
 *   one this device doesn't know, means UTC. All-day series are dates: expanded in UTC, exclusions
 *   matched by date.
 * - HOURLY rules step in elapsed time, not on the clock, as the server's do.
 * - An occurrence counts when it overlaps [from, to); a zero-length one when it starts in it.
 * - A start the rule itself doesn't produce isn't an occurrence.
 * - Nothing throws and nothing runs unbounded: a rule that doesn't parse, or can never match,
 *   yields nothing, and one expansion yields at most [MAX_OCCURRENCES].
 */
object Recurrence {
    const val MAX_OCCURRENCES = 10_000

    data class Span(val start: Long, val end: Long)

    private val DAY = Duration.ofDays(1).toMillis()

    fun expand(
        start: Long,
        end: Long,
        timeZone: String?,
        rrule: String,
        exDates: Collection<Long>,
        from: Long,
        to: Long,
        allDay: Boolean,
    ): List<Span> = runCatching {
        val duration = (end - start).coerceAtLeast(0)
        val span = if (allDay) duration + DAY else duration
        val hourly = rrule.uppercase().contains("FREQ=HOURLY")
        val zone: ZoneId = if (allDay || hourly) ZoneOffset.UTC else zoneOf(timeZone)
        val seed = ZonedDateTime.ofInstant(Instant.ofEpochMilli(start), zone)
        val lower = ZonedDateTime.ofInstant(Instant.ofEpochMilli(from - span - 1000), zone)
        val upper = ZonedDateTime.ofInstant(Instant.ofEpochMilli(to), zone)

        val excludedDays = if (allDay) exDates.map { LocalDate.ofEpochDay(Math.floorDiv(it, DAY)) }.toHashSet() else null
        val excluded = if (allDay) null else exDates.map { truncateToSeconds(it) }.toHashSet()

        val result = ArrayList<Span>()
        // getDates is bounded by its count; ical4j's lazy stream yields nothing when iterated
        // (only when collected), so the list is the safe form.
        for (occurrence in Recur<ZonedDateTime>(rrule).getDates(seed, lower, upper, MAX_OCCURRENCES * 2)) {
            if (result.size >= MAX_OCCURRENCES) break
            run {
                val s = occurrence.toInstant().toEpochMilli()
                if (s >= to) return@run
                val overlaps = if (span > 0) s + span > from else s >= from
                val skip = excludedDays?.contains(occurrence.withZoneSameInstant(ZoneOffset.UTC).toLocalDate())
                    ?: (truncateToSeconds(s) in excluded!!)
                if (overlaps && !skip && s >= start) result += Span(s, s + duration)
            }
        }
        result
    }.getOrDefault(emptyList())

    private fun zoneOf(id: String?): ZoneId =
        id?.let { runCatching { ZoneId.of(it) }.getOrNull() } ?: ZoneOffset.UTC

    private fun truncateToSeconds(millis: Long) = Math.floorDiv(millis, 1000L) * 1000L

    /** Midnight UTC of [date]: how an all-day date is stored. */
    fun dayMillis(date: LocalDate): Long = date.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli()

    /** The date an all-day value stands for — never converted through a zone. */
    fun dateOf(dayMillis: Long): LocalDate = Instant.ofEpochMilli(dayMillis).atZone(ZoneOffset.UTC).toLocalDate()

}
