package org.hyperstarit.twentyfourseven.calendar

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class RepeatRuleTest {
    private val berlin = ZoneId.of("Europe/Berlin")

    @Test
    fun theEditorsRules_RoundTrip() {
        listOf("FREQ=DAILY", "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR;COUNT=10", "FREQ=MONTHLY", "FREQ=YEARLY;INTERVAL=3")
            .forEach { assertEquals(it, RepeatRule.parse(it)!!.format(false, berlin)) }
    }

    @Test
    fun anythingElse_IsLeftAlone() {
        listOf("FREQ=MONTHLY;BYDAY=-1FR", "FREQ=MONTHLY;BYMONTHDAY=31", "FREQ=HOURLY", "FREQ=DAILY;BYDAY=MO", "nonsense", "FREQ=WEEKLY;BYDAY=1MO")
            .forEach { assertNull(it, RepeatRule.parse(it)) }
    }

    @Test
    fun until_IsTheLastDay_ForAllDay_AndItsEndInTheZone_Otherwise() {
        val rule = RepeatRule(RepeatRule.Freq.WEEKLY, days = setOf(DayOfWeek.TUESDAY), until = LocalDate.of(2026, 12, 31))
        assertEquals("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231", rule.format(true, berlin))
        assertEquals("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231T225959Z", rule.format(false, berlin))
        assertEquals(LocalDate.of(2026, 12, 31), RepeatRule.parse("FREQ=WEEKLY;UNTIL=20261231T225959Z", berlin)!!.until)
        val ny = ZoneId.of("America/New_York") // the day ends at 04:59:59Z the next day
        assertEquals(LocalDate.of(2026, 12, 31), RepeatRule.parse(rule.format(false, ny), ny)!!.until)
    }
}
