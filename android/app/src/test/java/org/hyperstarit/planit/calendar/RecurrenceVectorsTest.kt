package org.hyperstarit.planit.calendar

import java.io.File
import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The device expands repeats exactly as the server does: testdata/recurrence-vectors.json holds
 * the server's expansions (CalendarIT.Tests/RecurrenceVectorsTests checks the server against it).
 */
class RecurrenceVectorsTest {
    private fun ms(iso: String) = Instant.parse(iso).toEpochMilli()

    @Test
    fun everyVector_ExpandsAsOnTheServer() {
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "testdata/recurrence-vectors.json") }
            .first { it.isFile }
        val failures = mutableListOf<String>()
        for (v in Json.parseToJsonElement(file.readText()).jsonArray.map { it.jsonObject }) {
            val got = Recurrence.expand(
                start = ms(v["start"]!!.jsonPrimitive.content),
                end = ms(v["end"]!!.jsonPrimitive.content),
                timeZone = v["timeZone"]!!.jsonPrimitive.contentOrNull,
                rrule = v["rrule"]!!.jsonPrimitive.content,
                exDates = v["exDates"]!!.jsonArray.map { ms(it.jsonPrimitive.content) },
                from = ms(v["from"]!!.jsonPrimitive.content),
                to = ms(v["to"]!!.jsonPrimitive.content),
                allDay = v["allDay"]!!.jsonPrimitive.boolean,
            ).map { Instant.ofEpochMilli(it.start).toString() }
            val expected = v["expected"]!!.jsonArray.map { it.jsonPrimitive.content }
            if (got != expected) failures += "${v["name"]!!.jsonPrimitive.content}:\n  expected $expected\n  got      $got"
        }
        assertEquals(failures.joinToString("\n"), 0, failures.size)
    }
}
