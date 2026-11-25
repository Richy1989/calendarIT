package org.hyperstarit.twentyfourseven.widget

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.YearMonth
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class MonthModelTest {
    private fun o(title: String, start: Long, allDay: Boolean = false) = Occurrence(
        key = title, eventId = title, seriesId = null, recurrenceId = null, accountId = "a", calendarId = "c", calendarName = "P",
        title = title, location = null, description = null, start = start, end = start, allDay = allDay, timeZone = null,
        color = "#FF0000", recurrence = null, reminders = emptyList(), attendees = emptyList(), invitationStatus = null,
        organizerEmail = null, readOnly = false,
    )

    @Test
    fun theGrid_IsWholeWeeks_FromTheLocalesFirstDay() {
        val nov = YearMonth.of(2026, 11) // 1 Nov 2026 is a Sunday
        val monday = MonthModel.build(nov, DayOfWeek.MONDAY, LocalDate.of(2026, 11, 25), emptyMap(), 3)
        assertEquals(LocalDate.of(2026, 10, 26), monday.weeks.first().first().date)
        assertEquals(LocalDate.of(2026, 12, 6), monday.weeks.last().last().date)
        assertEquals(6, monday.weeks.size)
        val sunday = MonthModel.build(nov, DayOfWeek.SUNDAY, LocalDate.of(2026, 11, 25), emptyMap(), 3)
        assertEquals(5, sunday.weeks.size)
        assertEquals(listOf(DayOfWeek.SUNDAY, DayOfWeek.MONDAY), sunday.weekdays.take(2))
        assertTrue(sunday.weeks.flatten().single { it.today }.date == LocalDate.of(2026, 11, 25))
        assertTrue(sunday.weeks.flatten().filter { !it.inMonth }.all { it.date.monthValue != 11 })
    }

    @Test
    fun aFullDay_ShowsWhatFits_AllDayFirst_AndCountsTheRest() {
        val day = LocalDate.of(2026, 11, 25)
        val items = listOf(o("late", 300), o("early", 100), o("holiday", 0, allDay = true), o("noon", 200))
        val cell = MonthModel.build(YearMonth.of(2026, 11), DayOfWeek.MONDAY, day, mapOf(day to items), 3)
            .weeks.flatten().single { it.date == day }
        assertEquals(listOf("holiday", "early"), cell.chips.map { it.title }) // the third line says "+2"
        assertEquals(2, cell.more)

        val roomy = MonthModel.build(YearMonth.of(2026, 11), DayOfWeek.MONDAY, day, mapOf(day to items), 4)
            .weeks.flatten().single { it.date == day }
        assertEquals(4, roomy.chips.size)
        assertEquals(0, roomy.more)
    }
}
