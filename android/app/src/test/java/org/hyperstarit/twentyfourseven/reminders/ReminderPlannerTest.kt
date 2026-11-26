package org.hyperstarit.twentyfourseven.reminders

import org.hyperstarit.twentyfourseven.calendar.byKey
import java.time.Instant
import java.time.ZoneId
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.hyperstarit.twentyfourseven.data.db.TaskData
import org.junit.Assert.assertEquals
import org.junit.Test

class ReminderPlannerTest {
    private fun ms(iso: String) = Instant.parse(iso).toEpochMilli()
    private val berlin = ZoneId.of("Europe/Berlin")
    private val calendars = listOf(CalendarEntity("cal", "a", "P", null, "America/New_York", null, emptyList(), false, 0, "1")).byKey()

    private fun occurrence(start: String, allDay: Boolean = false, zone: String? = "Europe/Berlin", reminders: List<Reminder>, series: String? = null, rid: Long? = null) = Occurrence(
        key = "k", eventId = "e", seriesId = series, recurrenceId = rid, accountId = "a", calendarId = "cal", calendarName = "P",
        title = "Dentist", location = "Main St", description = null, start = ms(start), end = ms(start), allDay = allDay,
        timeZone = zone, color = null, recurrence = null, reminders = reminders, attendees = emptyList(), invitationStatus = null,
        organizerEmail = null, readOnly = false,
    )

    private fun plan(o: List<Occurrence> = emptyList(), t: List<TaskData> = emptyList()) =
        ReminderPlanner.plan(o, t, calendars, ms("2026-11-01T00:00:00Z"), ms("2026-12-31T00:00:00Z"), berlin)

    @Test
    fun timed_RingsMinutesBefore_OnlyNotificationOnes() {
        val p = plan(listOf(occurrence("2026-11-26T13:00:00Z", reminders = listOf(Reminder(15, "WebPush"), Reminder(60, "Email")))))
        assertEquals(listOf(ms("2026-11-26T12:45:00Z")), p.map { it.at }) // email is the server's
    }

    @Test
    fun allDay_CountsFromMidnight_InItsOwnZone_ElseItsCalendars() {
        val own = plan(listOf(occurrence("2026-11-26T00:00:00Z", allDay = true, reminders = listOf(Reminder(0, "WebPush")))))
        assertEquals(ms("2026-11-25T23:00:00Z"), own.single().at) // midnight in Berlin
        val calendarZone = plan(listOf(occurrence("2026-11-26T00:00:00Z", allDay = true, zone = null, reminders = listOf(Reminder(0, "WebPush")))))
        assertEquals(ms("2026-11-26T05:00:00Z"), calendarZone.single().at) // midnight in New York
    }

    @Test
    fun tasks_RingBeforeTheirDue_DateOnesFromNine_AndNotWhenDone() {
        fun task(due: String, allDay: Boolean, status: String = "NeedsAction") = TaskData(
            "t", "a", "cal", "Taxes", null, ms(due), allDay, "Europe/Berlin", status, null, null, null, 0, listOf(Reminder(30, "WebPush")), "1",
        )
        assertEquals(ms("2026-11-26T07:30:00Z"), plan(t = listOf(task("2026-11-26T00:00:00Z", true))).single().at) // 09:00 Berlin − 30 min
        assertEquals(ms("2026-11-26T15:30:00Z"), plan(t = listOf(task("2026-11-26T16:00:00Z", false))).single().at)
        assertEquals(0, plan(t = listOf(task("2026-11-26T16:00:00Z", false, status = "Completed"))).size)
    }

    @Test
    fun keys_NameTheOccurrence_SoReschedulingKeepsThem() {
        val a = plan(listOf(occurrence("2026-11-26T13:00:00Z", reminders = listOf(Reminder(15, "WebPush")), series = "s", rid = 1L)))
        val b = plan(listOf(occurrence("2026-11-26T14:00:00Z", reminders = listOf(Reminder(15, "WebPush")), series = "s", rid = 1L)))
        assertEquals(a.single().key, b.single().key) // the occurrence moved: same reminder, new time
    }

    @Test
    fun outsideTheWindow_NothingIsPlanned() {
        assertEquals(0, plan(listOf(occurrence("2027-03-01T13:00:00Z", reminders = listOf(Reminder(15, "WebPush"))))).size)
    }
}
