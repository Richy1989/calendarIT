package org.hyperstarit.planit.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kizitonwose.calendar.compose.ContentHeightMode
import com.kizitonwose.calendar.compose.HorizontalCalendar
import com.kizitonwose.calendar.compose.rememberCalendarState
import com.kizitonwose.calendar.core.CalendarDay
import com.kizitonwose.calendar.core.DayPosition
import com.kizitonwose.calendar.core.daysOfWeek
import java.time.LocalDate
import java.time.YearMonth

private const val SHOWN_PER_DAY = 3

/** A month grid: each day with its first few appointments; a tap opens the day. */
@Composable
fun MonthView(focus: LocalDate, shown: Shown?, formats: Formats, onMonthShown: (LocalDate) -> Unit, onDay: (LocalDate) -> Unit) {
    val today = LocalDate.now(formats.zone)
    val focusMonth = YearMonth.from(focus)
    val state = rememberCalendarState(
        startMonth = focusMonth.minusMonths(600),
        endMonth = focusMonth.plusMonths(600),
        firstVisibleMonth = focusMonth,
        firstDayOfWeek = formats.firstDayOfWeek,
    )
    // "Today" (or a day picked elsewhere) moves the grid; swiping moves the focus.
    LaunchedEffect(focusMonth) {
        if (state.firstVisibleMonth.yearMonth != focusMonth) state.animateScrollToMonth(focusMonth)
    }
    LaunchedEffect(state) {
        snapshotFlow { state.firstVisibleMonth.yearMonth }.collect { month ->
            if (month != YearMonth.from(focus)) onMonthShown(month.atDay(1))
        }
    }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
            daysOfWeek(formats.firstDayOfWeek).forEach { day ->
                Text(
                    formats.dayName(day), modifier = Modifier.weight(1f), textAlign = TextAlign.Center,
                    style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        HorizontalCalendar(
            state = state,
            contentHeightMode = ContentHeightMode.Fill,
            dayContent = { day -> MonthDay(day, shown?.on(day.date).orEmpty(), day.date == today, onDay) },
        )
    }
}

@Composable
private fun MonthDay(day: CalendarDay, items: List<org.hyperstarit.planit.calendar.Occurrence>, isToday: Boolean, onDay: (LocalDate) -> Unit) {
    val inMonth = day.position == DayPosition.MonthDate
    Column(
        Modifier
            .fillMaxSize()
            .border(0.5.dp, MaterialTheme.colorScheme.outlineVariant)
            .clickable { onDay(day.date) }
            .padding(2.dp),
        verticalArrangement = Arrangement.spacedBy(1.dp),
    ) {
        Box(
            Modifier
                .align(Alignment.CenterHorizontally)
                .size(22.dp)
                .clip(CircleShape)
                .background(if (isToday) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                day.date.dayOfMonth.toString(),
                fontSize = 12.sp,
                fontWeight = if (isToday) FontWeight.Bold else FontWeight.Normal,
                color = when {
                    isToday -> MaterialTheme.colorScheme.onPrimary
                    inMonth -> MaterialTheme.colorScheme.onSurface
                    else -> MaterialTheme.colorScheme.outline
                },
            )
        }
        items.take(SHOWN_PER_DAY).forEach { o ->
            val color = parseColor(o.color) ?: MaterialTheme.colorScheme.primary
            Text(
                o.title,
                maxLines = 1,
                overflow = TextOverflow.Clip,
                fontSize = 10.sp,
                lineHeight = 12.sp,
                color = if (o.allDay) onColor(color) else MaterialTheme.colorScheme.onSurface,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(3.dp))
                    .background(if (o.allDay) color else color.copy(alpha = 0.18f))
                    .padding(horizontal = 2.dp),
            )
        }
        if (items.size > SHOWN_PER_DAY) {
            Text("+${items.size - SHOWN_PER_DAY}", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
