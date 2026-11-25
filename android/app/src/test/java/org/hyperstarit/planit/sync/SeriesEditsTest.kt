package org.hyperstarit.planit.sync

import java.time.Instant
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.OverrideData
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The device's predictions follow the server's EventService (see SeriesSplitTests there). */
class SeriesEditsTest {
    private fun ms(iso: String) = Instant.parse(iso).toEpochMilli()
    private val start = ms("2026-08-03T07:00:00Z") // Mondays 09:00 Berlin
    private val fourth = ms("2026-08-24T07:00:00Z")

    private fun series(rule: String, exDates: List<Long> = emptyList(), overrides: List<OverrideData> = emptyList()) = SeriesEdits.Series(
        EventData(
            id = "s", accountId = "a", calendarId = "c", title = "Standup", description = null, location = null, categoryId = null,
            color = null, start = start, end = start + 3_600_000, allDay = false, timeZone = "Europe/Berlin", recurrence = rule,
            exDates = exDates, reminders = emptyList(), attendees = emptyList(), invitationStatus = null, organizerEmail = null, version = "1",
        ),
        overrides,
    )

    private fun override(at: Long) = OverrideData("o$at", "s", at, "edited", null, null, null, at + 600_000, at + 4_200_000, false, "Europe/Berlin", emptyList())

    @Test
    fun splitting_EndsTheOldSeriesJustBefore_AndCarriesWhatFollowsWhenUnmoved() {
        val s = series("FREQ=WEEKLY;BYDAY=MO", exDates = listOf(ms("2026-08-10T07:00:00Z"), ms("2026-08-31T07:00:00Z")), overrides = listOf(override(ms("2026-09-07T07:00:00Z"))))
        val (kept, next) = SeriesEdits.split(s, fourth, s.event.copy(id = "n", title = "Standup v2", start = fourth, end = fourth + 3_600_000))!!

        assertEquals("FREQ=WEEKLY;BYDAY=MO;UNTIL=20260824T065959Z", kept.event.recurrence)
        assertEquals(listOf(ms("2026-08-10T07:00:00Z")), kept.event.exDates)
        assertTrue(kept.overrides.isEmpty())
        assertEquals(listOf(ms("2026-08-31T07:00:00Z")), next.event.exDates)
        assertEquals(listOf("n"), next.overrides.map { it.seriesId })
    }

    @Test
    fun aCount_IsSharedOut_AndAMovedSlotDropsWhatFollowed() {
        val s = series("FREQ=WEEKLY;COUNT=6", exDates = listOf(ms("2026-08-31T07:00:00Z")))
        val (_, same) = SeriesEdits.split(s, fourth, s.event.copy(id = "n", start = fourth, end = fourth + 3_600_000))!!
        assertEquals("FREQ=WEEKLY;COUNT=3", same.event.recurrence) // 3 of 6 were before

        val (_, moved) = SeriesEdits.split(s, fourth, s.event.copy(id = "n", start = fourth + 3_600_000, end = fourth + 7_200_000))!!
        assertTrue(moved.event.exDates.isEmpty())
    }

    @Test
    fun onTheFirstOccurrence_ItIsTheWholeSeries() {
        val s = series("FREQ=WEEKLY")
        assertNull(SeriesEdits.split(s, start, s.event))
        assertNull(SeriesEdits.deleteFollowing(s, start))
    }

    @Test
    fun deletingTheFollowing_KeepsWhatCameBefore() {
        val s = series("FREQ=DAILY", overrides = listOf(override(ms("2026-08-04T07:00:00Z")), override(ms("2026-08-30T07:00:00Z"))))
        val kept = SeriesEdits.deleteFollowing(s, fourth)!!
        assertEquals("FREQ=DAILY;UNTIL=20260824T065959Z", kept.event.recurrence)
        assertEquals(listOf(ms("2026-08-04T07:00:00Z")), kept.overrides.map { it.recurrenceId })
    }

    @Test
    fun anAllDaySeries_EndsTheDayBefore() {
        assertEquals("20260823", SeriesEdits.untilBefore(ms("2026-08-24T00:00:00Z"), allDay = true))
    }

    @Test
    fun oneOccurrence_EditedOrDeleted_IsSkippedByTheRule() {
        val s = series("FREQ=WEEKLY")
        val edited = SeriesEdits.upsertOccurrence(s, fourth, override(fourth))
        assertEquals(listOf(fourth), edited.event.exDates)
        assertEquals(1, edited.overrides.size)
        val deleted = SeriesEdits.deleteOccurrence(edited, fourth)
        assertEquals(listOf(fourth), deleted.event.exDates)
        assertTrue(deleted.overrides.isEmpty())
    }
}
