package org.hyperstarit.twentyfourseven.caldav

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.time.zone.ZoneOffsetTransitionRule

/**
 * Dates and times in iCalendar, mapped to how the app stores them: epoch milliseconds UTC, an
 * all-day value as its date's midnight UTC (never run through a zone).
 */
object ICalTime {
    /** A DATE or DATE-TIME as stored: [zone] null for UTC. */
    data class Value(val millis: Long, val allDay: Boolean, val zone: String?)

    private val DATE = DateTimeFormatter.BASIC_ISO_DATE
    private val LOCAL = DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss")
    private const val DAY = 86_400_000L

    /**
     * The zone a TZID names, in the tz database's spelling — or null when this device can't tell
     * (then the time is read in [ZoneOffset.UTC]'s stead by the caller's fallback). Accepts vendor
     * prefixes ("/mozilla.org/20050126_1/Europe/Berlin") and Windows names ("W. Europe Standard Time").
     */
    fun zoneOf(tzid: String?): String? {
        val id = tzid?.trim()?.removeSurrounding("\"")?.takeIf { it.isNotEmpty() } ?: return null
        known(id)?.let { return it }
        val parts = id.split('/').filter { it.isNotEmpty() }
        for (n in 3 downTo 1) if (parts.size > n) known(parts.takeLast(n).joinToString("/"))?.let { return it }
        runCatching { android.icu.util.TimeZone.getIDForWindowsID(id, "001") }.getOrNull()?.let { known(it)?.let { z -> return z } }
        return null
    }

    private fun known(id: String): String? =
        if (id.equals("UTC", true) || id.equals("GMT", true) || id == "Z") "UTC"
        else runCatching { ZoneId.of(id).id }.getOrNull()?.takeIf { it.contains('/') || it == "UTC" }

    /**
     * One value of a DATE / DATE-TIME property. A floating time (no zone, no Z) is read on
     * [floating]'s clock — the calendar's zone, or this phone's.
     */
    fun parse(text: String, tzid: String?, valueType: String?, floating: ZoneId): Value? = runCatching {
        val v = text.trim()
        if (valueType.equals("DATE", true) || (v.length == 8 && v.all(Char::isDigit))) {
            val date = LocalDate.parse(v.take(8), DATE)
            return@runCatching Value(date.toEpochDay() * DAY, true, null)
        }
        if (v.endsWith("Z") || v.endsWith("z")) {
            val ldt = LocalDateTime.parse(v.dropLast(1).take(15), LOCAL)
            return@runCatching Value(ldt.toInstant(ZoneOffset.UTC).toEpochMilli(), false, null)
        }
        val ldt = LocalDateTime.parse(v.take(15), LOCAL)
        val zone = zoneOf(tzid)
        val zoneId = zone?.let(ZoneId::of) ?: floating
        Value(ldt.atZone(zoneId).toInstant().toEpochMilli(), false, zone ?: floating.id.takeIf { floating != ZoneOffset.UTC })
    }.getOrNull()

    /** Every value of a property (EXDATE and RDATE can hold several). */
    fun parseAll(p: ICalProperty, floating: ZoneId): List<Value> =
        ICal.splitList(p.value).mapNotNull { parse(it, p.param("TZID"), p.param("VALUE"), floating) }

    fun parse(p: ICalProperty?, floating: ZoneId): Value? = p?.let { parse(it.value, it.param("TZID"), it.param("VALUE"), floating) }

    /** The value and parameters to write for an instant (or a date when [allDay]) in [zone]. */
    fun format(millis: Long, allDay: Boolean, zone: String?): Pair<String, List<Pair<String, String>>> = when {
        allDay -> LocalDate.ofEpochDay(Math.floorDiv(millis, DAY)).format(DATE) to listOf("VALUE" to "DATE")
        zone == null || zone == "UTC" -> utc(millis) to emptyList()
        else -> {
            val z = runCatching { ZoneId.of(zone) }.getOrNull()
            if (z == null) utc(millis) to emptyList()
            else LocalDateTime.ofInstant(Instant.ofEpochMilli(millis), z).format(LOCAL) to listOf("TZID" to z.id)
        }
    }

    fun utc(millis: Long): String = LocalDateTime.ofInstant(Instant.ofEpochMilli(millis), ZoneOffset.UTC).format(LOCAL) + "Z"

    /** "-PT15M", "P1D", "-P1DT2H30M", "P2W" — null when it isn't a duration. */
    fun parseDuration(text: String): Duration? = runCatching {
        var s = text.trim().uppercase()
        val negative = s.startsWith('-')
        s = s.removePrefix("-").removePrefix("+")
        if (!s.startsWith('P')) return@runCatching null
        s = s.substring(1)
        var total = Duration.ZERO
        var inTime = false
        val num = StringBuilder()
        for (ch in s) {
            when {
                ch == 'T' -> inTime = true
                ch.isDigit() -> num.append(ch)
                else -> {
                    val n = num.toString().toLongOrNull() ?: return@runCatching null
                    num.clear()
                    total += when (ch) {
                        'W' -> Duration.ofDays(7 * n)
                        'D' -> Duration.ofDays(n)
                        'H' -> Duration.ofHours(n)
                        'M' -> if (inTime) Duration.ofMinutes(n) else return@runCatching null
                        'S' -> Duration.ofSeconds(n)
                        else -> return@runCatching null
                    }
                }
            }
        }
        if (num.isNotEmpty()) return@runCatching null
        if (negative) total.negated() else total
    }.getOrNull()

    /** A trigger [minutes] before: "-PT15M"; zero is "PT0S". */
    fun before(minutes: Int): String = if (minutes == 0) "PT0S" else if (minutes > 0) "-PT${minutes}M" else "PT${-minutes}M"

    /**
     * A VTIMEZONE for [zoneId] from this device's tz data: its yearly DST rules, or its fixed
     * offset. RFC 5545 wants one for every TZID a resource uses, and some servers refuse without.
     */
    fun vTimeZone(zoneId: String): ICalComponent? {
        val zone = runCatching { ZoneId.of(zoneId) }.getOrNull() ?: return null
        val rules = zone.rules
        val tz = ICalComponent("VTIMEZONE")
        tz.set("TZID", zone.id)
        val yearly = rules.transitionRules
        if (yearly.isEmpty()) {
            val offset = rules.getOffset(Instant.now())
            tz.components += ICalComponent("STANDARD").apply {
                set("DTSTART", "19700101T000000")
                set("TZOFFSETFROM", offset(offset))
                set("TZOFFSETTO", offset(offset))
            }
            return tz
        }
        for (r in yearly) tz.components += observance(r)
        return tz
    }

    private fun observance(r: ZoneOffsetTransitionRule): ICalComponent {
        val daylight = r.offsetAfter != r.standardOffset
        val onset = r.createTransition(1970).dateTimeBefore // local time in the offset before
        val day = r.dayOfMonthIndicator
        val dow = r.dayOfWeek
        val byDay = dow?.let { it.name.take(2) }
        val rule = buildString {
            append("FREQ=YEARLY;BYMONTH=").append(r.month.value)
            when {
                dow == null -> append(";BYMONTHDAY=").append(day)
                day == -1 -> append(";BYDAY=-1").append(byDay)
                day < 0 -> append(";BYDAY=").append(byDay).append(";BYMONTHDAY=").append(((day - 6)..day).joinToString(","))
                else -> append(";BYDAY=").append(byDay).append(";BYMONTHDAY=").append((day..(day + 6).coerceAtMost(31)).joinToString(","))
            }
        }
        return ICalComponent(if (daylight) "DAYLIGHT" else "STANDARD").apply {
            set("DTSTART", onset.format(LOCAL))
            set("RRULE", rule)
            set("TZOFFSETFROM", offset(r.offsetBefore))
            set("TZOFFSETTO", offset(r.offsetAfter))
        }
    }

    private fun offset(o: ZoneOffset): String {
        val total = o.totalSeconds
        val sign = if (total < 0) "-" else "+"
        val abs = Math.abs(total)
        val s = "%s%02d%02d".format(sign, abs / 3600, abs / 60 % 60)
        return if (abs % 60 != 0) s + "%02d".format(abs % 60) else s
    }
}
