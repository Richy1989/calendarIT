package org.hyperstarit.planit.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.time.temporal.TemporalAdjusters
import kotlinx.coroutines.delay
import org.hyperstarit.planit.calendar.Occurrence
import org.hyperstarit.planit.calendar.TimelineLayout

private val HOUR_HEIGHT = 56.dp
private val GUTTER = 44.dp
private const val PAGES = 20_000
private const val MIDDLE = PAGES / 2

/**
 * Days on an hour grid, [days] at a time (1 = day view, 7 = week view): all-day appointments in a
 * row on top, timed ones placed by time, side by side where they overlap. Swipe for the next days.
 */
@Composable
fun TimelineView(
    days: Int,
    focus: LocalDate,
    shown: Shown?,
    formats: Formats,
    onFocus: (LocalDate) -> Unit,
    onDay: (LocalDate) -> Unit,
    onOpen: (Occurrence) -> Unit,
) {
    // Page 0 of the pager is anchored so that the middle page holds today's day (or week).
    val anchor = remember(days, formats.firstDayOfWeek) {
        val today = LocalDate.now(formats.zone)
        if (days == 7) today.with(TemporalAdjusters.previousOrSame(formats.firstDayOfWeek)) else today
    }
    fun pageOf(date: LocalDate) = MIDDLE + Math.floorDiv(ChronoUnit.DAYS.between(anchor, date), days.toLong()).toInt()
    fun startOf(page: Int): LocalDate = anchor.plusDays((page - MIDDLE).toLong() * days)

    val pager = rememberPagerState(initialPage = pageOf(focus)) { PAGES }
    LaunchedEffect(focus) {
        val target = pageOf(focus)
        if (pager.currentPage != target) pager.animateScrollToPage(target)
    }
    LaunchedEffect(pager) {
        snapshotFlow { pager.settledPage }.collect { page ->
            if (pageOf(focus) != page) onFocus(startOf(page))
        }
    }
    val density = androidx.compose.ui.platform.LocalDensity.current

    HorizontalPager(state = pager, beyondViewportPageCount = 1) { page ->
        val dates = (0 until days).map { startOf(page).plusDays(it.toLong()) }
        // Each page opens at 07:00, where most days begin.
        val scroll = rememberScrollState(with(density) { (HOUR_HEIGHT * 7).roundToPx() })
        Column(Modifier.fillMaxSize()) {
            DayHeaders(dates, formats, onDay)
            AllDayRow(dates, shown, onOpen)
            HorizontalDivider()
            Row(Modifier.fillMaxSize().verticalScroll(scroll)) {
                HourGutter(formats)
                dates.forEach { date ->
                    DayColumn(date, shown?.on(date).orEmpty().filter { !it.allDay }, formats, onOpen, Modifier.weight(1f))
                }
            }
        }
    }
}

@Composable
private fun DayHeaders(dates: List<LocalDate>, formats: Formats, onDay: (LocalDate) -> Unit) {
    val today = LocalDate.now(formats.zone)
    Row(Modifier.fillMaxWidth().padding(start = GUTTER, top = 4.dp, bottom = 4.dp)) {
        dates.forEach { date ->
            Column(Modifier.weight(1f).clickable(enabled = dates.size > 1) { onDay(date) }, horizontalAlignment = Alignment.CenterHorizontally) {
                Text(formats.dayName(date.dayOfWeek), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Box(
                    Modifier.size(28.dp).clip(CircleShape)
                        .background(if (date == today) MaterialTheme.colorScheme.primary else Color.Transparent),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        date.dayOfMonth.toString(),
                        fontWeight = FontWeight.SemiBold,
                        color = if (date == today) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface,
                    )
                }
            }
        }
    }
}

@Composable
private fun AllDayRow(dates: List<LocalDate>, shown: Shown?, onOpen: (Occurrence) -> Unit) {
    val perDay = dates.map { date -> shown?.on(date).orEmpty().filter { it.allDay } }
    if (perDay.all { it.isEmpty() }) return
    Row(Modifier.fillMaxWidth().padding(start = GUTTER, bottom = 2.dp)) {
        perDay.forEach { items ->
            Column(Modifier.weight(1f).padding(horizontal = 1.dp)) {
                items.take(3).forEach { o -> Chip(o, onOpen, Modifier.fillMaxWidth().padding(vertical = 1.dp)) }
                if (items.size > 3) Text("+${items.size - 3}", fontSize = 10.sp)
            }
        }
    }
}

@Composable
private fun Chip(o: Occurrence, onOpen: (Occurrence) -> Unit, modifier: Modifier) {
    val color = parseColor(o.color) ?: MaterialTheme.colorScheme.primary
    Text(
        o.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontSize = 11.sp, color = onColor(color),
        modifier = modifier.clip(RoundedCornerShape(4.dp)).background(color).clickable { onOpen(o) }.padding(horizontal = 4.dp, vertical = 1.dp),
    )
}

@Composable
private fun HourGutter(formats: Formats) {
    Column(Modifier.width(GUTTER)) {
        (0 until 24).forEach { h ->
            Box(Modifier.height(HOUR_HEIGHT).fillMaxWidth()) {
                if (h > 0) {
                    Text(
                        formats.hour(h), fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.align(Alignment.TopEnd).offset(y = (-7).dp).padding(end = 6.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun DayColumn(date: LocalDate, items: List<Occurrence>, formats: Formats, onOpen: (Occurrence) -> Unit, modifier: Modifier) {
    val dayStart = date.atStartOfDay(formats.zone).toInstant().toEpochMilli()
    val dayEnd = date.plusDays(1).atStartOfDay(formats.zone).toInstant().toEpochMilli()
    val dayLength = (dayEnd - dayStart).toFloat() // 23 or 25 hours on DST days
    val placed = remember(items, date) {
        TimelineLayout.place(
            items.map { TimelineLayout.Item(it, maxOf(it.start, dayStart), minOf(maxOf(it.end, it.start), dayEnd)) },
            minLength = 20 * 60_000L,
        )
    }
    val now by produceState(System.currentTimeMillis()) {
        while (true) { delay(60_000); value = System.currentTimeMillis() }
    }
    BoxWithConstraints(modifier.height(HOUR_HEIGHT * 24)) {
        val total = HOUR_HEIGHT * 24
        Column {
            (0 until 24).forEach { _ ->
                Box(Modifier.height(HOUR_HEIGHT).fillMaxWidth()) { HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f)) }
            }
        }
        Box(Modifier.fillMaxHeight().width(0.5.dp).background(MaterialTheme.colorScheme.outlineVariant))
        placed.forEach { p ->
            val top = total * ((p.start - dayStart) / dayLength)
            val height = (total * ((maxOf(p.end, p.start + 20 * 60_000L) - p.start) / dayLength)).coerceAtLeast(18.dp)
            val width = maxWidth / p.columns
            EventBlock(p.value, formats, onOpen, Modifier.offset(x = width * p.column, y = top).width(width).height(height))
        }
        if (now in dayStart until dayEnd) {
            Box(
                Modifier.offset(y = total * ((now - dayStart) / dayLength)).fillMaxWidth().height(2.dp)
                    .background(MaterialTheme.colorScheme.error),
            )
        }
    }
}

@Composable
private fun EventBlock(o: Occurrence, formats: Formats, onOpen: (Occurrence) -> Unit, modifier: Modifier) {
    val color = parseColor(o.color) ?: MaterialTheme.colorScheme.primary
    Column(
        modifier.padding(1.dp).clip(RoundedCornerShape(4.dp)).background(color).clickable { onOpen(o) }.padding(horizontal = 3.dp, vertical = 1.dp),
    ) {
        Text(o.title, fontSize = 11.sp, lineHeight = 13.sp, fontWeight = FontWeight.Medium, color = onColor(color), maxLines = 2, overflow = TextOverflow.Ellipsis)
        Text(formats.time(o.start), fontSize = 10.sp, lineHeight = 12.sp, color = onColor(color).copy(alpha = 0.85f), maxLines = 1)
    }
}

