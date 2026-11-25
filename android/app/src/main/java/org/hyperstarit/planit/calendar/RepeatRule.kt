package org.hyperstarit.planit.calendar

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * The repeat rules the editor builds: every N days/weeks/months/years, on chosen weekdays, ending
 * never, after a count or on a date. A rule with anything else (BYSETPOS, BYMONTHDAY, …) isn't one
 * of these: [parse] gives null and the editor leaves it as it is — like the web editor, it never
 * rewrites a rule it doesn't fully understand.
 */
data class RepeatRule(
    val freq: Freq,
    val interval: Int = 1,
    val days: Set<DayOfWeek> = emptySet(),
    val count: Int? = null,
    val until: LocalDate? = null,
) {
    enum class Freq { DAILY, WEEKLY, MONTHLY, YEARLY }

    /** The RRULE. UNTIL is the last day itself for all-day series, else the end of that day in [zone]. */
    fun format(allDay: Boolean, zone: ZoneId): String = buildList {
        add("FREQ=$freq")
        if (interval > 1) add("INTERVAL=$interval")
        if (freq == Freq.WEEKLY && days.isNotEmpty()) add("BYDAY=" + days.sorted().joinToString(",") { CODES.getValue(it) })
        when {
            count != null -> add("COUNT=$count")
            until != null -> add(
                "UNTIL=" + if (allDay) until.format(DateTimeFormatter.BASIC_ISO_DATE)
                else until.plusDays(1).atStartOfDay(zone).minusSeconds(1).withZoneSameInstant(ZoneOffset.UTC)
                    .format(DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss'Z'")),
            )
        }
    }.joinToString(";")

    companion object {
        private val CODES = mapOf(
            DayOfWeek.MONDAY to "MO", DayOfWeek.TUESDAY to "TU", DayOfWeek.WEDNESDAY to "WE", DayOfWeek.THURSDAY to "TH",
            DayOfWeek.FRIDAY to "FR", DayOfWeek.SATURDAY to "SA", DayOfWeek.SUNDAY to "SU",
        )
        private val DAYS = CODES.entries.associate { (k, v) -> v to k }

        /** [zone]: the series' own, to read a timed UNTIL (a UTC instant) back as the date it ends on. */
        fun parse(rule: String?, zone: ZoneId = ZoneOffset.UTC): RepeatRule? {
            if (rule.isNullOrBlank()) return null
            var freq: Freq? = null
            var interval = 1
            var days = emptySet<DayOfWeek>()
            var count: Int? = null
            var until: LocalDate? = null
            for (part in rule.split(';').filter { it.isNotBlank() }) {
                val (key, value) = part.split('=', limit = 2).takeIf { it.size == 2 } ?: return null
                when (key.uppercase()) {
                    "FREQ" -> freq = runCatching { Freq.valueOf(value.uppercase()) }.getOrNull() ?: return null
                    "INTERVAL" -> interval = value.toIntOrNull()?.takeIf { it in 1..999 } ?: return null
                    "BYDAY" -> days = value.split(',').map { DAYS[it.uppercase()] ?: return null }.toSet() // "1MO" isn't plain
                    "COUNT" -> count = value.toIntOrNull()?.takeIf { it > 0 } ?: return null
                    "UNTIL" -> until = runCatching {
                        if (value.length > 8) {
                            java.time.LocalDateTime.parse(value.trimEnd('Z', 'z'), DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss"))
                                .let { if (value.endsWith("Z", ignoreCase = true)) it.atZone(ZoneOffset.UTC).withZoneSameInstant(zone) else it.atZone(zone) }
                                .toLocalDate()
                        } else {
                            LocalDate.parse(value, DateTimeFormatter.BASIC_ISO_DATE)
                        }
                    }.getOrNull() ?: return null
                    "WKST" -> Unit
                    else -> return null
                }
            }
            val f = freq ?: return null
            if (days.isNotEmpty() && f != Freq.WEEKLY) return null
            return RepeatRule(f, interval, days, count, until)
        }
    }
}
