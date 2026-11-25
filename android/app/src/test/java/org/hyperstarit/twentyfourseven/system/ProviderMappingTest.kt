package org.hyperstarit.twentyfourseven.system

import java.time.Instant
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ProviderMappingTest {
    private fun ms(iso: String) = Instant.parse(iso).toEpochMilli()

    private fun event(start: String, end: String, allDay: Boolean = false, rule: String? = null) = EventData(
        id = "e", accountId = "a", calendarId = "c", title = "T", description = null, location = "L", categoryId = null, color = null,
        start = ms(start), end = ms(end), allDay = allDay, timeZone = "Europe/Berlin", recurrence = rule, exDates = emptyList(),
        reminders = emptyList(), attendees = emptyList(), invitationStatus = null, organizerEmail = null, version = "1",
    )

    @Test
    fun aSeries_IsARuleWithADuration_AndOnlyItsDeletionsAsExdate() {
        val row = ProviderMapping.eventRow(event("2026-11-02T08:00:00Z", "2026-11-02T08:30:00Z", rule = "FREQ=DAILY"), listOf(ms("2026-11-04T08:00:00Z")), null)
        assertEquals("FREQ=DAILY", row["rrule"])
        assertEquals("P1800S", row["duration"])
        assertNull(row["dtend"])
        assertEquals("20261104T080000Z", row["exdate"])
        assertEquals("Europe/Berlin", row["eventTimezone"])
        assertEquals(0, row["hasAlarm"])
    }

    @Test
    fun allDay_IsMidnightUtc_WithAnExclusiveEnd_AndComesBackTheSame() {
        val e = event("2026-12-24T00:00:00Z", "2026-12-26T00:00:00Z", allDay = true) // 24–26 Dec
        val row = ProviderMapping.eventRow(e, emptyList(), null)
        assertEquals("UTC", row["eventTimezone"])
        assertEquals(ms("2026-12-27T00:00:00Z"), row["dtend"])
        val back = ProviderMapping.incoming(row)
        assertEquals(e.start, back.start)
        assertEquals(e.end, back.end)
    }

    @Test
    fun whatOtherAppsWrite_IsRead() {
        assertEquals(5_400_000L, ProviderMapping.parseDuration("PT1H30M"))
        assertEquals(86_400_000L, ProviderMapping.parseDuration("P1D"))
        assertEquals(3_600_000L, ProviderMapping.parseDuration("P3600S"))
        assertEquals(listOf(ms("2026-12-24T08:00:00Z"), ms("2026-12-25T00:00:00Z")), ProviderMapping.parseExDates("20261224T080000Z,20261225"))
        val back = ProviderMapping.incoming(mapOf("title" to "", "dtstart" to ms("2026-11-02T08:00:00Z"), "duration" to "PT45M", "rrule" to "FREQ=WEEKLY", "allDay" to 0, "eventTimezone" to "Europe/Vienna"))
        assertEquals("(No title)", back.title)
        assertEquals(ms("2026-11-02T08:45:00Z"), back.end)
        assertEquals("Europe/Vienna", back.timeZone)
    }

    @Test
    fun theFingerprint_ChangesWithTheContent() {
        val a = ProviderMapping.eventRow(event("2026-11-02T08:00:00Z", "2026-11-02T09:00:00Z"), emptyList(), null)
        val b = a + ("title" to "Other")
        assertEquals(ProviderMapping.fingerprint(a), ProviderMapping.fingerprint(LinkedHashMap(a)))
        assert(ProviderMapping.fingerprint(a) != ProviderMapping.fingerprint(b))
    }
}
