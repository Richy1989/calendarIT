package org.hyperstarit.twentyfourseven.sync

import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import org.hyperstarit.twentyfourseven.calendar.Recurrence
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.OverrideData

/**
 * What a change to part of a repeating series does to it, predicted on the device the way the
 * server will do it (`EventService`), so the calendar shows the result at once. The server's
 * answer replaces the prediction at the next sync.
 */
object SeriesEdits {
    data class Series(val event: EventData, val overrides: List<OverrideData>)

    private const val HOUR = 3_600_000L

    fun truncate(millis: Long) = Math.floorDiv(millis, 1000L) * 1000L

    /** One occurrence edited: the rule now skips it, and an edited copy stands in. */
    fun upsertOccurrence(s: Series, recurrenceId: Long, edited: OverrideData): Series {
        val rid = truncate(recurrenceId)
        return Series(
            s.event.copy(exDates = (s.event.exDates + rid).distinct().sorted()),
            s.overrides.filter { truncate(it.recurrenceId) != rid } + edited.copy(seriesId = s.event.id, recurrenceId = rid),
        )
    }

    /** One occurrence deleted (with its edited copy, if it had one). */
    fun deleteOccurrence(s: Series, recurrenceId: Long): Series {
        val rid = truncate(recurrenceId)
        return Series(s.event.copy(exDates = (s.event.exDates + rid).distinct().sorted()), s.overrides.filter { truncate(it.recurrenceId) != rid })
    }

    /** Whether anything of the series lies before [recurrenceId]: else "this and following" is all of it. */
    fun hasOccurrenceBefore(s: Series, recurrenceId: Long): Boolean {
        val rid = truncate(recurrenceId)
        if (s.overrides.any { it.recurrenceId < rid }) return true
        val e = s.event
        val rule = e.recurrence ?: return false
        return Recurrence.expand(e.start, e.end ?: (e.start + HOUR), e.timeZone, rule, e.exDates, e.start, rid, e.allDay)
            .any { it.start < rid }
    }

    /** "This and following", deleting: the series ends just before; null when nothing is left. */
    fun deleteFollowing(s: Series, recurrenceId: Long): Series? {
        if (!hasOccurrenceBefore(s, recurrenceId)) return null
        val rid = truncate(recurrenceId)
        return Series(
            s.event.copy(
                recurrence = withEnd(s.event.recurrence!!, until = untilBefore(rid, s.event.allDay)),
                exDates = s.event.exDates.filter { it < rid },
            ),
            s.overrides.filter { it.recurrenceId < rid },
        )
    }

    /**
     * "This and following", editing: the old series ends just before, [next] (a new series with
     * its own id) starts there. When it keeps the old rule and slot, the deleted and edited
     * occurrences from there on go with it, and a COUNT is shared out. Null old part when nothing
     * came before (then it is a whole-series edit; the caller does that instead).
     */
    fun split(s: Series, recurrenceId: Long, next: EventData): Pair<Series, Series>? {
        if (!hasOccurrenceBefore(s, recurrenceId)) return null
        val rid = truncate(recurrenceId)
        val master = s.event
        val rule = master.recurrence!!
        val sameRule = next.recurrence == rule
        val unmoved = sameRule && truncate(next.start) == rid && next.allDay == master.allDay &&
            (next.allDay || (next.timeZone ?: master.timeZone) == master.timeZone)
        var nextRule = next.recurrence
        if (sameRule) countOf(rule)?.let { count -> nextRule = withEnd(rule, count = maxOf(1, count - generatedBefore(master, rid))) }
        val kept = Series(
            master.copy(recurrence = withEnd(rule, until = untilBefore(rid, master.allDay)), exDates = master.exDates.filter { it < rid }),
            s.overrides.filter { it.recurrenceId < rid },
        )
        val carried = unmoved && nextRule != null
        val started = Series(
            next.copy(
                recurrence = nextRule,
                timeZone = next.timeZone ?: master.timeZone,
                exDates = if (carried) master.exDates.filter { it >= rid } else emptyList(),
                version = null,
            ),
            if (carried) s.overrides.filter { it.recurrenceId >= rid }.map { it.copy(seriesId = next.id) } else emptyList(),
        )
        return kept to started
    }

    /** How many instances the rule produced before [rid], exclusions included (COUNT counts them). */
    private fun generatedBefore(master: EventData, rid: Long): Int =
        Recurrence.expand(master.start, master.end ?: (master.start + HOUR), master.timeZone, master.recurrence!!, emptyList(), master.start, rid, master.allDay)
            .count { it.start < rid }

    /** An UNTIL keeping everything before [rid]: the day before (a date) for all-day, else the second before, in UTC. */
    fun untilBefore(rid: Long, allDay: Boolean): String {
        val at = Instant.ofEpochMilli(rid).atOffset(ZoneOffset.UTC)
        return if (allDay) at.toLocalDate().minusDays(1).format(DateTimeFormatter.BASIC_ISO_DATE)
        else at.minusSeconds(1).format(DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss'Z'"))
    }

    fun withEnd(rule: String, until: String? = null, count: Int? = null): String {
        val parts = rule.split(';').filter { it.isNotBlank() && !it.uppercase().startsWith("COUNT=") && !it.uppercase().startsWith("UNTIL=") }.toMutableList()
        if (until != null) parts += "UNTIL=$until" else if (count != null) parts += "COUNT=$count"
        return parts.joinToString(";")
    }

    fun countOf(rule: String): Int? =
        rule.split(';').firstOrNull { it.uppercase().startsWith("COUNT=") }?.substringAfter('=')?.toIntOrNull()
}
