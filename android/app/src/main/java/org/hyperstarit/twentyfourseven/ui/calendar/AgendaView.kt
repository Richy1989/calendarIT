package org.hyperstarit.twentyfourseven.ui.calendar

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.time.LocalDate
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.ui.theme.EmptyState
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel

/** Everything coming up, day by day from the focus date; empty days are left out. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun AgendaView(focus: LocalDate, shown: Shown?, formats: Formats, onOpen: (Occurrence) -> Unit) {
    val c = Brand.colors
    val today = LocalDate.now(formats.zone)
    val days = shown?.byDate?.keys?.filter { !it.isBefore(focus) }?.sorted().orEmpty()
    val state = rememberLazyListState()
    LaunchedEffect(focus) { state.scrollToItem(0) }
    if (shown != null && days.isEmpty()) {
        EmptyState(Glyphs.calendar, "Nothing planned", "Nothing from ${formats.longDate(focus)} on. Tap + to add an appointment.", Modifier.padding(top = 48.dp))
        return
    }
    LazyColumn(Modifier.fillMaxSize(), state = state, contentPadding = PaddingValues(bottom = 96.dp)) {
        days.forEach { date ->
            stickyHeader(key = "d$date") {
                Row(
                    Modifier.fillMaxWidth().background(c.bg.copy(alpha = 0.94f)).padding(start = 20.dp, end = 20.dp, top = 14.dp, bottom = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    SectionLabel(
                        when (date) {
                            today -> "Today · ${formats.longDate(date)}"
                            today.plusDays(1) -> "Tomorrow · ${formats.longDate(date)}"
                            else -> formats.longDate(date)
                        },
                        color = if (date == today) c.link else c.textDim,
                    )
                }
            }
            items(shown!!.on(date), key = { "$date/${it.key}" }) { o -> AgendaRow(o, formats, onOpen) }
        }
    }
}

@Composable
private fun AgendaRow(o: Occurrence, formats: Formats, onOpen: (Occurrence) -> Unit) {
    val c = Brand.colors
    val color = parseColor(o.color) ?: c.accent
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 3.dp).height(IntrinsicSize.Min)
            .clip(RoundedCornerShape(14.dp)).background(c.surface).border(1.dp, c.hairline, RoundedCornerShape(14.dp))
            .clickable { onOpen(o) },
    ) {
        Box(Modifier.width(4.dp).fillMaxHeight().background(color))
        Column(Modifier.width(72.dp).padding(start = 12.dp, top = 12.dp, bottom = 12.dp)) {
            if (o.allDay) {
                Text("All day", style = Brand.data, color = c.textDim)
            } else {
                Text(formats.time(o.start), style = Brand.data, fontWeight = FontWeight.SemiBold, color = c.text)
                Text(formats.time(o.end), style = Brand.data, color = c.textFaint)
            }
        }
        Column(Modifier.weight(1f).padding(start = 8.dp, end = 14.dp, top = 11.dp, bottom = 11.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (o.recurrence != null || o.seriesId != null) {
                    Icon(Glyphs.repeat, "Repeats", tint = c.textFaint, modifier = Modifier.padding(end = 6.dp).width(13.dp))
                }
                Text(o.title, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.SemiBold, color = c.text, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            val meta = listOfNotNull(o.location?.takeIf { it.isNotBlank() }, o.calendarName).joinToString(" · ")
            Text(meta, style = MaterialTheme.typography.bodySmall, color = c.textDim, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 2.dp))
        }
    }
}
