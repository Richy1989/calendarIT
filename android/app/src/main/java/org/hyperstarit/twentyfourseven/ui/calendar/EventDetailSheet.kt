package org.hyperstarit.twentyfourseven.ui.calendar

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
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.material3.Icon
import org.hyperstarit.twentyfourseven.ui.theme.Banner
import org.hyperstarit.twentyfourseven.ui.theme.GhostButton
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.Segmented
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.calendar.Recurrence

/** An appointment's details, with edit and delete — or, for an invitation, the answer to it. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EventDetailSheet(
    o: Occurrence,
    formats: Formats,
    onDismiss: () -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
    onRespond: (String) -> Unit,
) {
    val c = Brand.colors
    val color = parseColor(o.color) ?: c.accent
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        containerColor = c.surface,
        scrimColor = c.overlay.copy(alpha = 0.55f),
        dragHandle = { Box(Modifier.padding(top = 10.dp, bottom = 6.dp).size(40.dp, 4.dp).clip(RoundedCornerShape(2.dp)).background(c.hairlineStrong)) },
    ) {
        Column(
            Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Row(verticalAlignment = Alignment.Top, modifier = Modifier.padding(bottom = 10.dp)) {
                Box(Modifier.padding(top = 7.dp).size(14.dp).clip(RoundedCornerShape(4.dp)).background(color))
                Column(Modifier.padding(start = 14.dp)) {
                    Text(o.title, style = MaterialTheme.typography.headlineSmall, color = c.text)
                    val date = if (o.allDay) Recurrence.dateOf(o.start) else java.time.Instant.ofEpochMilli(o.start).atZone(formats.zone).toLocalDate()
                    Text(formats.longDate(date), style = MaterialTheme.typography.bodyMedium, color = c.textDim, modifier = Modifier.padding(top = 2.dp))
                }
            }
            Detail(Glyphs.clock, formats.span(o))
            o.recurrence?.let { Detail(Glyphs.repeat, describeRule(it)) }
            o.location?.takeIf { it.isNotBlank() }?.let { Detail(Glyphs.pin, it) }
            Detail(Glyphs.calendar, o.calendarName + if (o.readOnly) " · read-only" else "", dot = color)
            if (!o.allDay && o.timeZone != null && o.timeZone != formats.zone.id) Detail(Glyphs.globe, o.timeZone)
            if (o.reminders.isNotEmpty()) {
                Detail(Glyphs.bell, o.reminders.joinToString("\n") { reminderText(it.minutesBefore, o.allDay) + if (it.channel == "Email") " · email" else "" })
            }
            o.organizerEmail?.let { Detail(Glyphs.mail, "Invited by $it") }
            if (o.attendees.isNotEmpty()) Detail(Glyphs.users, o.attendees.joinToString("\n") { "${it.name ?: it.email} · ${statusWord(it.status)}" })
            o.description?.takeIf { it.isNotBlank() }?.let { Detail(Glyphs.text, it) }
            if (!o.readOnly && o.invitationStatus != null) {
                SectionLabel("Going?", Modifier.padding(top = 14.dp, bottom = 6.dp))
                Segmented(
                    listOf("Accepted" to "Yes", "Tentative" to "Maybe", "Declined" to "No"),
                    o.invitationStatus, { s: String? -> s?.let(onRespond) }, Modifier.fillMaxWidth(),
                )
            }
            Spacer(Modifier.height(16.dp))
            if (!o.readOnly) {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    // A received invitation is the organizer's to change: only answering and removing it.
                    if (o.invitationStatus == null) PrimaryButton("Edit", onEdit, Modifier.weight(1f), icon = Glyphs.edit)
                    GhostButton("Delete", onDelete, Modifier.weight(if (o.invitationStatus == null) 0.8f else 1f), icon = Glyphs.trash, danger = true)
                }
            } else {
                Banner("This calendar is read-only.", warning = false)
            }
        }
    }
}

@Composable
private fun Detail(icon: androidx.compose.ui.graphics.vector.ImageVector, value: String, dot: androidx.compose.ui.graphics.Color? = null) {
    val c = Brand.colors
    Row(Modifier.fillMaxWidth().padding(vertical = 7.dp), verticalAlignment = Alignment.Top) {
        Icon(icon, null, tint = c.textFaint, modifier = Modifier.size(20.dp))
        if (dot != null) Box(Modifier.padding(start = 14.dp, top = 6.dp).size(8.dp).clip(RoundedCornerShape(4.dp)).background(dot))
        Text(value, style = MaterialTheme.typography.bodyLarge, color = c.text, modifier = Modifier.padding(start = if (dot != null) 8.dp else 16.dp))
    }
}

private fun statusWord(status: String): String = when (status) {
    "Accepted" -> "going"
    "Declined" -> "not going"
    "Tentative" -> "maybe"
    "NeedsAction" -> "hasn't answered"
    else -> status
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
    minutes == 10080 -> "1 week before"
    minutes % 1440 == 0 -> (minutes / 1440).let { if (it == 1) "1 day before" else "$it days before" }
    minutes % 60 == 0 -> (minutes / 60).let { if (it == 1) "1 hour before" else "$it hours before" }
    else -> "$minutes minutes before"
}
