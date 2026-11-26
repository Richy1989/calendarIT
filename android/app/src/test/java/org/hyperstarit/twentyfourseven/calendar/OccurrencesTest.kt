package org.hyperstarit.twentyfourseven.calendar

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.CategoryEntity
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.OverrideData
import org.junit.Assert.assertEquals
import org.junit.Test

class OccurrencesTest {
    private val berlin = ZoneId.of("Europe/Berlin")
    private val ny = ZoneId.of("America/New_York")
    private fun ms(iso: String) = Instant.parse(iso).toEpochMilli()

    private val calendar = CalendarEntity("cal", "a", "Personal", "#112233", null, "catDefault", emptyList(), false, 0, "1")
    private val categories = listOf(
        CategoryEntity("catDefault", "a", "Default", "#00AA00", "1"),
        CategoryEntity("work", "a", "Work", "#FF0000", "1"),
    ).byKey()

    private fun event(
        id: String, start: String, end: String?, allDay: Boolean = false, rule: String? = null, exDates: List<String> = emptyList(),
        category: String? = null, calendarId: String = "cal",
    ) = EventData(
        id = id, accountId = "a", calendarId = calendarId, title = id, description = null, location = null, categoryId = category,
        color = null, start = ms(start), end = end?.let(::ms), allDay = allDay, timeZone = "Europe/Berlin", recurrence = rule,
        exDates = exDates.map(::ms), reminders = emptyList(), attendees = emptyList(), invitationStatus = null, organizerEmail = null, version = "1",
    )

    private fun build(events: List<EventData>, overrides: List<OverrideData> = emptyList(), range: DateRange, zone: ZoneId = berlin, hidden: Set<String> = emptySet()) =
        Occurrences.build(events, overrides, listOf(calendar).byKey(), categories, hidden, range, zone)

    private val week = DateRange(LocalDate.of(2026, 11, 2), LocalDate.of(2026, 11, 9))

    @Test
    fun aSeries_ShowsItsOccurrences_WithItsEditedOneInPlace() {
        val series = event("standup", "2026-11-02T08:00:00Z", "2026-11-02T08:15:00Z", rule = "FREQ=DAILY;COUNT=5", exDates = listOf("2026-11-04T08:00:00Z"))
        val moved = OverrideData("o", "a", "standup", ms("2026-11-04T08:00:00Z"), "Standup (late)", null, null, null,
            ms("2026-11-04T10:00:00Z"), ms("2026-11-04T10:15:00Z"), false, "Europe/Berlin", emptyList())

        val shown = build(listOf(series), listOf(moved), week)

        assertEquals(listOf("standup", "standup", "Standup (late)", "standup", "standup"), shown.map { it.title })
        assertEquals(ms("2026-11-04T10:00:00Z"), shown[2].start)
        assertEquals(ms("2026-11-04T08:00:00Z"), shown[2].recurrenceId) // still that occurrence of the series
        assertEquals(5, shown.map { it.key }.toSet().size)
    }

    @Test
    fun anAllDayEvent_IsTheSameDateWherever_ItIsSeen() {
        val holiday = event("holiday", "2026-11-05T00:00:00Z", "2026-11-05T00:00:00Z", allDay = true)
        for (zone in listOf(berlin, ny, ZoneId.of("Pacific/Kiritimati"))) {
            val byDate = Occurrences.byDate(build(listOf(holiday), range = week, zone = zone), week, zone)
            assertEquals(zone.id, setOf(LocalDate.of(2026, 11, 5)), byDate.keys)
        }
    }

    @Test
    fun multiDayAppointments_AppearOnEveryDayTheyCover() {
        val trip = event("trip", "2026-11-03T00:00:00Z", "2026-11-05T00:00:00Z", allDay = true) // 3 dates, end inclusive
        val night = event("night", "2026-11-06T21:00:00Z", "2026-11-07T05:00:00Z") // 22:00 – 06:00 Berlin
        val byDate = Occurrences.byDate(build(listOf(trip, night), range = week), week, berlin)
        assertEquals(listOf(3, 4, 5).map { LocalDate.of(2026, 11, it) }, byDate.filterValues { v -> v.any { it.title == "trip" } }.keys.sorted())
        assertEquals(listOf(6, 7).map { LocalDate.of(2026, 11, it) }, byDate.filterValues { v -> v.any { it.title == "night" } }.keys.sorted())
    }

    @Test
    fun endingAtMidnight_DoesntReachIntoTheNextDay() {
        val late = event("late", "2026-11-03T22:00:00Z", "2026-11-03T23:00:00Z") // 23:00 – 00:00 Berlin
        val byDate = Occurrences.byDate(build(listOf(late), range = week), week, berlin)
        assertEquals(setOf(LocalDate.of(2026, 11, 3)), byDate.keys)
    }

    @Test
    fun colors_ComeFromTheCategory_ElseTheCalendarsDefault() {
        val shown = build(
            listOf(event("own", "2026-11-03T08:00:00Z", "2026-11-03T09:00:00Z", category = "work"), event("plain", "2026-11-03T10:00:00Z", "2026-11-03T11:00:00Z")),
            range = week,
        )
        assertEquals("#FF0000", shown.single { it.title == "own" }.color)
        assertEquals("#00AA00", shown.single { it.title == "plain" }.color)
    }

    @Test
    fun hiddenCalendars_ShowNothing() {
        val e = event("x", "2026-11-03T08:00:00Z", "2026-11-03T09:00:00Z")
        assertEquals(0, build(listOf(e), range = week, hidden = setOf("cal")).size)
    }
}
