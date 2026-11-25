package org.hyperstarit.planit.calendar

import org.junit.Assert.assertEquals
import org.junit.Test

class TimelineLayoutTest {
    private fun item(name: String, start: Int, end: Int) = TimelineLayout.Item(name, start.toLong(), end.toLong())

    @Test
    fun overlapping_SitSideBySide_AndTheRestKeepTheFullWidth() {
        val placed = TimelineLayout.place(
            listOf(item("a", 9, 11), item("b", 10, 12), item("c", 10, 11), item("d", 13, 14), item("e", 11, 12)),
        ).associateBy { it.value }
        // a, b, c overlap at 10:00 (three columns); e takes a's freed column; d stands alone.
        assertEquals(3, placed["a"]!!.columns)
        assertEquals(setOf(0, 1, 2), listOf("a", "b", "c").map { placed[it]!!.column }.toSet())
        assertEquals(3, placed["e"]!!.columns)
        assertEquals(placed["a"]!!.column, placed["e"]!!.column)
        assertEquals(1, placed["d"]!!.columns)
    }

    @Test
    fun backToBack_AreNotOverlaps() {
        val placed = TimelineLayout.place(listOf(item("a", 9, 10), item("b", 10, 11)))
        assertEquals(listOf(1, 1), placed.map { it.columns })
    }
}
