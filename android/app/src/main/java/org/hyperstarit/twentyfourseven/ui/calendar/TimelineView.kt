package org.hyperstarit.twentyfourseven.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.border
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import org.hyperstarit.twentyfourseven.ui.theme.Display
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.time.temporal.TemporalAdjusters
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.calendar.TimelineLayout

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
    focus: CalendarFocus,
    shown: Shown?,
    formats: Formats,
    onFocus: (LocalDate, Long, ViewMode) -> Unit,
    onDay: (LocalDate) -> Unit,
    onOpen: (Occurrence) -> Unit,
    onCreateAt: ((LocalDate, java.time.LocalTime) -> Unit)? = null,
) {
    // Page 0 of the pager is anchored so that the middle page holds today's day (or week).
    val anchor = remember(days, formats.firstDayOfWeek) {
        val today = LocalDate.now(formats.zone)
        if (days == 7) today.with(TemporalAdjusters.previousOrSame(formats.firstDayOfWeek)) else today
    }
    fun pageOf(date: LocalDate) = MIDDLE + Math.floorDiv(ChronoUnit.DAYS.between(anchor, date), days.toLong()).toInt()
    fun startOf(page: Int): LocalDate = anchor.plusDays((page - MIDDLE).toLong() * days)

    val pager = rememberPagerState(initialPage = pageOf(focus.date)) { PAGES }
    val latestDate by rememberUpdatedState(focus.date)
    val mode = if (days == 7) ViewMode.WEEK else ViewMode.DAY
    LaunchedEffect(pager, anchor, focus.navigationId) {
        val target = pageOf(focus.date)
        // Keep observing the pager if a new gesture interrupts this animation.
        coroutineScope {
            launch { pager.animateScrollToPage(target) }
        }
        snapshotFlow { if (pager.isScrollInProgress) null else pager.settledPage }.collect { page ->
            if (page != null && pageOf(latestDate) != page) {
                onFocus(startOf(page), focus.navigationId, mode)
            }
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
            Box(Modifier.fillMaxWidth().height(1.dp).background(Brand.colors.hairline))
            Row(Modifier.fillMaxSize().verticalScroll(scroll)) {
                HourGutter(formats)
                dates.forEach { date ->
                    DayColumn(date, shown?.on(date).orEmpty().filter { !it.allDay }, formats, onOpen, onCreateAt, Modifier.weight(1f))
                }
            }
        }
    }
}

@Composable
private fun DayHeaders(dates: List<LocalDate>, formats: Formats, onDay: (LocalDate) -> Unit) {
    val c = Brand.colors
    val today = LocalDate.now(formats.zone)
    Row(Modifier.fillMaxWidth().padding(start = GUTTER, top = 6.dp, bottom = 6.dp)) {
        dates.forEach { date ->
            val isToday = date == today
            val number = @Composable {
                Box(
                    Modifier.padding(top = 3.dp).size(30.dp).clip(CircleShape).then(if (isToday) Modifier.background(c.grad) else Modifier),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        date.dayOfMonth.toString(),
                        fontFamily = Display, fontSize = 15.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = if (isToday) c.onAccent else c.text,
                    )
                }
            }
            // A single day has room for its whole name beside the number: "Thursday 12".
            if (dates.size == 1) {
                Row(
                    Modifier.weight(1f).padding(vertical = 2.dp),
                    horizontalArrangement = Arrangement.Center,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        formats.fullDayName(date.dayOfWeek),
                        fontFamily = Display, fontSize = 15.sp, fontWeight = FontWeight.SemiBold,
                        color = if (isToday) c.link else c.text,
                        modifier = Modifier.padding(top = 3.dp, end = 6.dp),
                    )
                    number()
                }
                return@forEach
            }
            Column(
                Modifier.weight(1f).clip(RoundedCornerShape(10.dp)).clickable(enabled = dates.size > 1) { onDay(date) }.padding(vertical = 2.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(formats.dayName(date.dayOfWeek).uppercase().take(3), style = Brand.label, color = if (isToday) c.link else c.textFaint)
                number()
            }
        }
    }
}

@Composable
private fun AllDayRow(dates: List<LocalDate>, shown: Shown?, onOpen: (Occurrence) -> Unit) {
    val perDay = dates.map { date -> shown?.on(date).orEmpty().filter { it.allDay } }
    if (perDay.all { it.isEmpty() }) return
    Row(Modifier.fillMaxWidth().padding(bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text("all-day", style = Brand.data, fontSize = 9.sp, maxLines = 1, color = Brand.colors.textFaint, modifier = Modifier.width(GUTTER).padding(start = 4.dp))
        perDay.forEach { items ->
            Column(Modifier.weight(1f).padding(horizontal = 1.dp)) {
                items.take(3).forEach { o -> Chip(o, onOpen, Modifier.fillMaxWidth().padding(vertical = 1.dp)) }
                if (items.size > 3) Text("+${items.size - 3}", style = Brand.data, fontSize = 10.sp, color = Brand.colors.textFaint)
            }
        }
    }
}

@Composable
private fun Chip(o: Occurrence, onOpen: (Occurrence) -> Unit, modifier: Modifier) {
    val c = Brand.colors
    val color = parseColor(o.color) ?: c.accent
    Row(
        modifier.height(20.dp).clip(RoundedCornerShape(6.dp)).background(c.tint(color, 0.3f)).clickable { onOpen(o) },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.width(3.dp).fillMaxHeight().background(color))
        Text(
            o.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontSize = 11.sp, fontWeight = FontWeight.Medium, color = c.text,
            modifier = Modifier.padding(horizontal = 5.dp),
        )
    }
}

@Composable
private fun HourGutter(formats: Formats) {
    val c = Brand.colors
    Column(Modifier.width(GUTTER)) {
        (0 until 24).forEach { h ->
            Box(Modifier.height(HOUR_HEIGHT).fillMaxWidth()) {
                if (h > 0) {
                    Text(
                        formats.hour(h), style = Brand.data, fontSize = 10.sp, color = if (h % 6 == 0) c.textDim else c.textFaint,
                        modifier = Modifier.align(Alignment.TopEnd).offset(y = (-7).dp).padding(end = 8.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun DayColumn(
    date: LocalDate,
    items: List<Occurrence>,
    formats: Formats,
    onOpen: (Occurrence) -> Unit,
    onCreateAt: ((LocalDate, java.time.LocalTime) -> Unit)?,
    modifier: Modifier,
) {
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
    val hourPx = with(androidx.compose.ui.platform.LocalDensity.current) { HOUR_HEIGHT.toPx() }
    // A long press on the grid starts a new appointment at that half hour.
    val press = if (onCreateAt == null) Modifier else Modifier.pointerInput(date) {
        detectTapGestures(onLongPress = { at ->
            val halfHours = (at.y / (hourPx / 2)).toInt().coerceIn(0, 47)
            onCreateAt(date, java.time.LocalTime.of(halfHours / 2, (halfHours % 2) * 30))
        })
    }
    BoxWithConstraints(modifier.height(HOUR_HEIGHT * 24).then(press)) {
        val total = HOUR_HEIGHT * 24
        val c = Brand.colors
        // The hour rail as on the web: hairlines, with morning, midday and evening a step brighter.
        Box(
            Modifier.fillMaxSize().background(if (date == LocalDate.now(formats.zone)) c.accent.copy(alpha = 0.05f) else Color.Transparent).drawBehind {
                val step = size.height / 24f
                for (h in 0 until 24) {
                    val strong = h % 6 == 0 && h > 0
                    drawLine(if (strong) c.hairlineStrong else c.hairline.copy(alpha = 0.7f), Offset(0f, h * step), Offset(size.width, h * step), 1f)
                }
                drawLine(c.hairline, Offset(0f, 0f), Offset(0f, size.height), 1f)
            },
        )
        placed.forEach { p ->
            val top = total * ((p.start - dayStart) / dayLength)
            val height = (total * ((maxOf(p.end, p.start + 20 * 60_000L) - p.start) / dayLength)).coerceAtLeast(18.dp)
            val width = maxWidth / p.columns
            EventBlock(p.value, formats, onOpen, Modifier.offset(x = width * p.column, y = top).width(width).height(height))
        }
        if (now in dayStart until dayEnd) {
            val nowY = with(androidx.compose.ui.platform.LocalDensity.current) { (total * ((now - dayStart) / dayLength)).roundToPx() }
            val c = Brand.colors
            Box(Modifier.offset { androidx.compose.ui.unit.IntOffset(0, nowY) }.fillMaxWidth().height(2.dp).background(c.highlight))
            Box(
                Modifier.offset { androidx.compose.ui.unit.IntOffset(-5.dp.roundToPx(), nowY - 4.dp.roundToPx()) }.size(10.dp)
                    .clip(CircleShape).background(c.highlight),
            )
        }
    }
}

@Composable
private fun EventBlock(o: Occurrence, formats: Formats, onOpen: (Occurrence) -> Unit, modifier: Modifier) {
    val c = Brand.colors
    val color = parseColor(o.color) ?: c.accent
    Row(
        modifier.padding(start = 1.dp, end = 2.dp, top = 1.dp, bottom = 1.dp).clip(RoundedCornerShape(7.dp))
            .background(c.tint(color, 0.26f)).border(1.dp, color.copy(alpha = 0.35f), RoundedCornerShape(7.dp)).clickable { onOpen(o) },
    ) {
        Box(Modifier.width(3.dp).fillMaxHeight().background(color))
        Column(Modifier.padding(horizontal = 4.dp, vertical = 2.dp)) {
            Text(o.title, fontSize = 11.sp, lineHeight = 13.sp, fontWeight = FontWeight.SemiBold, color = c.text, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(formats.time(o.start), style = Brand.data, fontSize = 9.5.sp, lineHeight = 12.sp, color = c.textDim, maxLines = 1)
        }
    }
}
