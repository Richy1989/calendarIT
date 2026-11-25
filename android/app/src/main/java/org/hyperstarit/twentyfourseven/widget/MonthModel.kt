package org.hyperstarit.twentyfourseven.widget

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.YearMonth
import java.time.temporal.TemporalAdjusters
import org.hyperstarit.twentyfourseven.calendar.DateRange
import org.hyperstarit.twentyfourseven.calendar.Occurrence

/** One appointment as a month cell shows it. */
data class Chip(val title: String, val color: String?, val allDay: Boolean)

data class DayCell(val date: LocalDate, val inMonth: Boolean, val today: Boolean, val chips: List<Chip>, val more: Int)

/** A month as the widget draws it: whole weeks (4–6), each day with what fits of its appointments. */
data class MonthGrid(val month: YearMonth, val weekdays: List<DayOfWeek>, val weeks: List<List<DayCell>>)

object MonthModel {
    /** The dates the grid covers: from the week [month] starts in to the end of the week it ends in. */
    fun range(month: YearMonth, firstDay: DayOfWeek): DateRange {
        val from = month.atDay(1).with(TemporalAdjusters.previousOrSame(firstDay))
        val last = month.atEndOfMonth().with(TemporalAdjusters.nextOrSame(firstDay.plus(6)))
        return DateRange(from, last.plusDays(1))
    }

    /**
     * [perDay]: how many appointments a cell has room for; the rest are counted as "+n". All-day
     * ones come first (as in the app), then by time.
     */
    fun build(month: YearMonth, firstDay: DayOfWeek, today: LocalDate, byDate: Map<LocalDate, List<Occurrence>>, perDay: Int): MonthGrid {
        val range = range(month, firstDay)
        val days = generateSequence(range.from) { it.plusDays(1) }.takeWhile { it.isBefore(range.to) }.toList()
        val weeks = days.chunked(7).map { week ->
            week.map { date ->
                val items = byDate[date].orEmpty().sortedWith(compareBy({ !it.allDay }, { it.start }, { it.title }))
                val shown = if (items.size > perDay) maxOf(0, perDay - 1) else items.size // the last line says "+n"
                DayCell(
                    date = date,
                    inMonth = YearMonth.from(date) == month,
                    today = date == today,
                    chips = items.take(shown).map { Chip(it.title, it.color, it.allDay) },
                    more = items.size - shown,
                )
            }
        }
        return MonthGrid(month, (0 until 7).map { firstDay.plus(it.toLong()) }, weeks)
    }
}
