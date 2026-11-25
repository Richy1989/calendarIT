package org.hyperstarit.planit.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
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
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import java.time.LocalDate
import org.hyperstarit.planit.calendar.Occurrence

/** Everything coming up, day by day from the focus date; empty days are left out. */
@Composable
fun AgendaView(focus: LocalDate, shown: Shown?, formats: Formats, onOpen: (Occurrence) -> Unit) {
    val today = LocalDate.now(formats.zone)
    val days = shown?.byDate?.keys?.filter { !it.isBefore(focus) }?.sorted().orEmpty()
    val state = rememberLazyListState()
    LaunchedEffect(focus) { state.scrollToItem(0) }
    if (shown != null && days.isEmpty()) {
        Box(Modifier.fillMaxSize().padding(32.dp)) {
            Text("Nothing planned from ${formats.longDate(focus)} on.", color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        return
    }
    LazyColumn(Modifier.fillMaxSize(), state = state) {
        days.forEach { date ->
            item(key = "d$date") {
                Text(
                    if (date == today) "Today · ${formats.longDate(date)}" else formats.longDate(date),
                    style = MaterialTheme.typography.titleSmall,
                    color = if (date == today) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.padding(start = 16.dp, top = 16.dp, bottom = 4.dp),
                )
            }
            items(shown!!.on(date), key = { "$date/${it.key}" }) { o -> AgendaRow(o, formats, onOpen) }
        }
    }
}

@Composable
private fun AgendaRow(o: Occurrence, formats: Formats, onOpen: (Occurrence) -> Unit) {
    Row(Modifier.fillMaxWidth().clickable { onOpen(o) }.padding(horizontal = 16.dp, vertical = 6.dp)) {
        Box(
            Modifier.width(4.dp).height(40.dp).clip(RoundedCornerShape(2.dp))
                .background(parseColor(o.color) ?: MaterialTheme.colorScheme.primary),
        )
        Column(Modifier.padding(start = 12.dp).fillMaxHeight()) {
            Text(o.title, style = MaterialTheme.typography.bodyLarge)
            Text(
                formats.span(o) + (o.location?.takeIf { it.isNotBlank() }?.let { " · $it" } ?: ""),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
