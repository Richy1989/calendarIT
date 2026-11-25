package org.hyperstarit.planit.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import org.hyperstarit.planit.calendar.Occurrence
import org.hyperstarit.planit.calendar.Recurrence

/** An appointment's details. Read-only for now: editing comes with the editor. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EventDetailSheet(o: Occurrence, formats: Formats, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 24.dp).padding(bottom = 32.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(16.dp).clip(RoundedCornerShape(4.dp)).background(parseColor(o.color) ?: MaterialTheme.colorScheme.primary))
                Text(o.title, style = MaterialTheme.typography.headlineSmall, modifier = Modifier.padding(start = 12.dp))
            }
            val date = if (o.allDay) Recurrence.dateOf(o.start) else java.time.Instant.ofEpochMilli(o.start).atZone(formats.zone).toLocalDate()
            Detail(formats.longDate(date), formats.span(o))
            o.recurrence?.let { Detail("Repeats", describeRule(it)) }
            o.location?.takeIf { it.isNotBlank() }?.let { Detail("Location", it) }
            Detail("Calendar", o.calendarName + if (o.readOnly) " (read-only)" else "")
            if (!o.allDay && o.timeZone != null && o.timeZone != formats.zone.id) Detail("Time zone", o.timeZone)
            if (o.reminders.isNotEmpty()) {
                Detail("Reminders", o.reminders.joinToString("\n") { reminderText(it.minutesBefore, o.allDay) })
            }
            o.organizerEmail?.let { Detail("Invited by", it + (o.invitationStatus?.let { s -> " · you: $s" } ?: "")) }
            if (o.attendees.isNotEmpty()) Detail("Guests", o.attendees.joinToString("\n") { "${it.name ?: it.email} · ${it.status}" })
            o.description?.takeIf { it.isNotBlank() }?.let { Detail("Notes", it) }
        }
    }
}

@Composable
private fun Detail(label: String, value: String) {
    Column {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyLarge)
    }
}

/** A rule in words for the common cases; anything else as written. */
fun describeRule(rule: String): String {
    val parts = rule.split(';').mapNotNull { p -> p.split('=', limit = 2).takeIf { it.size == 2 }?.let { it[0].uppercase() to it[1] } }.toMap()
    val interval = parts["INTERVAL"]?.toIntOrNull() ?: 1
    val unit = when (parts["FREQ"]) {
        "DAILY" -> "day"
        "WEEKLY" -> "week"
        "MONTHLY" -> "month"
        "YEARLY" -> "year"
        else -> return rule
    }
    val every = if (interval == 1) "Every $unit" else "Every $interval ${unit}s"
    val days = parts["BYDAY"]?.split(',')?.joinToString(", ") { d ->
        mapOf("MO" to "Mon", "TU" to "Tue", "WE" to "Wed", "TH" to "Thu", "FR" to "Fri", "SA" to "Sat", "SU" to "Sun")[d.takeLast(2)]
            ?.let { name -> d.dropLast(2).takeIf { it.isNotEmpty() }?.let { "$it. $name" } ?: name } ?: d
    }
    val end = parts["COUNT"]?.let { ", $it times" }
        ?: parts["UNTIL"]?.let { u -> ", until " + u.take(8).let { "${it.substring(6, 8)}.${it.substring(4, 6)}.${it.substring(0, 4)}" } }
        ?: ""
    return every + (days?.let { " on $it" } ?: "") + end
}

fun reminderText(minutes: Int, allDay: Boolean): String = when {
    minutes == 0 -> if (allDay) "At midnight" else "At the start"
    minutes % 1440 == 0 -> "${minutes / 1440} day(s) before"
    minutes % 60 == 0 -> "${minutes / 60} hour(s) before"
    else -> "$minutes minutes before"
}
