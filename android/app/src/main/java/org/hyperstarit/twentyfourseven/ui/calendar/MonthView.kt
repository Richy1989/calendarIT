package org.hyperstarit.twentyfourseven.ui.calendar

import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
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
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch

private const val SHOWN_PER_DAY = 3
private const val MONTHS_EACH_SIDE = 600L

/** A month grid: each day with its first few appointments; a tap opens the day. */
@Composable
fun MonthView(
    focus: CalendarFocus,
    shown: Shown?,
    formats: Formats,
    onMonthShown: (LocalDate, Long, ViewMode) -> Unit,
    onDay: (LocalDate) -> Unit,
) {
    val today = LocalDate.now(formats.zone)
    val focusMonth = YearMonth.from(focus.date)
    // The calendar library recreates its scroll state when any of these inputs change.
    val initialMonth = remember { focusMonth }
    val startMonth = remember { initialMonth.minusMonths(MONTHS_EACH_SIDE) }
    val endMonth = remember { initialMonth.plusMonths(MONTHS_EACH_SIDE) }
    val state = rememberCalendarState(
        startMonth = startMonth,
        endMonth = endMonth,
        firstVisibleMonth = initialMonth,
        firstDayOfWeek = formats.firstDayOfWeek,
    )
    val latestMonth by rememberUpdatedState(focusMonth)
    LaunchedEffect(state, focus.navigationId) {
        // Observe after the requested move. A gesture can cancel the move and still settle.
        coroutineScope {
            launch {
                if (focusMonth < state.startMonth) state.startMonth = focusMonth.minusMonths(MONTHS_EACH_SIDE)
                if (focusMonth > state.endMonth) state.endMonth = focusMonth.plusMonths(MONTHS_EACH_SIDE)
                state.scrollToMonth(focusMonth)
            }
        }
        snapshotFlow { if (state.isScrollInProgress) null else state.firstVisibleMonth.yearMonth }.collect { month ->
            if (month != null && month != latestMonth) {
                onMonthShown(month.atDay(1), focus.navigationId, ViewMode.MONTH)
            }
        }
    }
    val c = Brand.colors
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 6.dp)) {
            daysOfWeek(formats.firstDayOfWeek).forEach { day ->
                Text(
                    formats.dayName(day).uppercase().take(3), modifier = Modifier.weight(1f), textAlign = TextAlign.Center,
                    style = Brand.label, color = c.textFaint,
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
private fun MonthDay(day: CalendarDay, items: List<org.hyperstarit.twentyfourseven.calendar.Occurrence>, isToday: Boolean, onDay: (LocalDate) -> Unit) {
    val c = Brand.colors
    val inMonth = day.position == DayPosition.MonthDate
    Column(
        Modifier
            .fillMaxSize()
            .background(if (isToday) c.accent.copy(alpha = 0.07f) else Color.Transparent)
            .drawBehind {
                // Hairlines on the top and left only, so neighbouring cells never draw a double line.
                drawLine(c.hairline, Offset(0f, 0f), Offset(size.width, 0f), 1f)
                drawLine(c.hairline, Offset(0f, 0f), Offset(0f, size.height), 1f)
            }
            .clickable { onDay(day.date) }
            .padding(horizontal = 2.dp, vertical = 3.dp)
            .alpha(if (inMonth) 1f else 0.45f),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Box(
            Modifier
                .align(Alignment.CenterHorizontally)
                .size(24.dp)
                .clip(CircleShape)
                .then(if (isToday) Modifier.background(c.grad) else Modifier),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                day.date.dayOfMonth.toString(),
                style = Brand.data,
                fontSize = 12.sp,
                fontWeight = if (isToday) FontWeight.Bold else FontWeight.Medium,
                color = when {
                    isToday -> c.onAccent
                    inMonth -> c.text
                    else -> c.textFaint
                },
            )
        }
        items.take(SHOWN_PER_DAY).forEach { o ->
            val color = parseColor(o.color) ?: c.accent
            if (o.allDay) {
                Row(
                    Modifier.fillMaxWidth().height(15.dp).clip(RoundedCornerShape(4.dp)).background(color.copy(alpha = 0.26f)),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(Modifier.width(2.5.dp).fillMaxHeight().background(color))
                    Text(
                        o.title, maxLines = 1, overflow = TextOverflow.Clip, fontSize = 10.sp, lineHeight = 12.sp,
                        fontWeight = FontWeight.Medium, color = c.text, modifier = Modifier.padding(horizontal = 3.dp),
                    )
                }
            } else {
                Row(Modifier.fillMaxWidth().height(15.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.padding(start = 2.dp, end = 3.dp).size(5.dp).clip(CircleShape).background(color))
                    Text(o.title, maxLines = 1, overflow = TextOverflow.Clip, fontSize = 10.sp, lineHeight = 12.sp, color = c.textDim)
                }
            }
        }
        if (items.size > SHOWN_PER_DAY) {
            Text("+${items.size - SHOWN_PER_DAY}", style = Brand.data, fontSize = 10.sp, color = c.textFaint, modifier = Modifier.padding(start = 3.dp))
        }
    }
}
