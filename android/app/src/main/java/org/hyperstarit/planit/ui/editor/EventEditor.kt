package org.hyperstarit.planit.ui.editor

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.TextStyle
import javax.inject.Inject
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.hyperstarit.planit.calendar.RepeatRule
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.CategoryEntity
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.data.db.Reminder
import org.hyperstarit.planit.sync.LocalChanges
import org.hyperstarit.planit.ui.calendar.Formats
import org.hyperstarit.planit.ui.calendar.describeRule
import org.hyperstarit.planit.ui.calendar.reminderText

@HiltViewModel
class EventEditorViewModel @Inject constructor(
    private val db: PlanItDatabase,
    private val changes: LocalChanges,
) : ViewModel() {
    var form by mutableStateOf(EventForm(ZoneId.systemDefault()))
        private set
    var calendars by mutableStateOf<List<CalendarEntity>>(emptyList())
        private set
    var categories by mutableStateOf<List<CategoryEntity>>(emptyList())
        private set
    var ready by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
    private var target: EditTarget? = null
    private var session: Any? = null
    private var base: EventData? = null

    /**
     * Starts the form for [target]. The view model outlives the dialog, so each opening passes its
     * own [session]: opening the same target again (the same new slot) starts fresh too.
     */
    fun open(target: EditTarget, session: Any) {
        if (this.session === session) return
        this.session = session
        this.target = target
        form = EventForm(ZoneId.systemDefault())
        ready = false
        error = null
        base = null
        val form = form
        viewModelScope.launch {
            val all = db.calendars().observeAll().first().filter { !it.readOnly }
            categories = db.categories().observeAll().first()
            when (target) {
                is EditTarget.New -> {
                    val calendar = all.firstOrNull()
                    calendars = all
                    form.calendarId = calendar?.id
                    val time = target.time ?: LocalTime.of(9, 0)
                    form.startDate = target.date; form.startTime = time
                    val end = target.date.atTime(time).plusHours(1)
                    form.endDate = end.toLocalDate(); form.endTime = end.toLocalTime()
                    // A new timed event starts with its calendar's default reminders, as on the web.
                    form.reminders = calendar?.defaultReminders.orEmpty()
                }
                is EditTarget.Existing -> {
                    val o = target.occurrence
                    val master = db.events().live(o.seriesId ?: o.eventId)?.data ?: return@launch
                    base = master
                    // Moving between accounts isn't a move: only this account's calendars.
                    calendars = all.filter { it.accountId == master.accountId }
                    if (target.scope == Scope.ALL) {
                        form.fill(master, master.start, master.end ?: master.start, master.allDay, master.title, master.location, master.description, master.categoryId, master.reminders)
                    } else {
                        form.fill(master, o.start, o.end, o.allDay, o.title, o.location, o.description, categoryOf(o.eventId, master), o.reminders)
                    }
                }
            }
            ready = true
        }
    }

    private suspend fun categoryOf(eventId: String, master: EventData): String? =
        if (eventId == master.id) master.categoryId
        else (db.events().overrideEdits(master.id) .map { it.data } + db.events().serverOverrides(master.id).map { it.data })
            .firstOrNull { it.id == eventId }?.categoryId ?: master.categoryId

    val isSeriesPart get() = (target as? EditTarget.Existing)?.scope.let { it == Scope.ONE }

    fun save(done: () -> Unit) {
        form.error()?.let { error = it; return }
        val t = target ?: return
        viewModelScope.launch {
            when (t) {
                is EditTarget.New -> {
                    val calendar = calendars.first { it.id == form.calendarId }
                    val blank = EventData(
                        id = "", accountId = calendar.accountId, calendarId = calendar.id, title = "", description = null, location = null,
                        categoryId = null, color = null, start = 0, end = null, allDay = false, timeZone = form.zone.id, recurrence = null,
                        exDates = emptyList(), reminders = emptyList(), attendees = emptyList(), invitationStatus = null, organizerEmail = null, version = null,
                    )
                    changes.createEvent(form.toEvent(blank))
                }
                is EditTarget.Existing -> {
                    val master = base ?: return@launch
                    val o = t.occurrence
                    when (t.scope) {
                        null, Scope.ALL -> changes.updateEvent(form.toEvent(master))
                        Scope.ONE -> changes.editOccurrence(master.id, o.recurrenceId!!, form.toEvent(master).copy(recurrence = null))
                        Scope.FOLLOWING -> changes.editFollowing(master.id, o.recurrenceId!!, form.toEvent(master))
                    }
                }
            }
            done()
        }
    }
}

private val REMINDER_CHOICES = listOf(0, 5, 10, 15, 30, 60, 120, 1440, 2880, 10080)

/** Creating or editing an appointment, full screen. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EventEditor(target: EditTarget, formats: Formats, onClose: () -> Unit, viewModel: EventEditorViewModel = hiltViewModel()) {
    val session = remember { Any() }
    viewModel.open(target, session)
    val f = viewModel.form
    var picking by remember { mutableStateOf<String?>(null) } // "startDate", "startTime", "endDate", "endTime", "until"
    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Scaffold(
            topBar = {
                TopAppBar(
                    navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.Default.Close, contentDescription = "Cancel") } },
                    title = {
                        Text(
                            when {
                                target is EditTarget.New -> "New appointment"
                                (target as EditTarget.Existing).scope == Scope.ONE -> "Edit this occurrence"
                                target.scope == Scope.FOLLOWING -> "Edit this and following"
                                else -> "Edit appointment"
                            },
                        )
                    },
                    actions = { TextButton(onClick = { viewModel.save(onClose) }, enabled = viewModel.ready) { Text("Save") } },
                )
            },
        ) { padding ->
            Column(
                Modifier.fillMaxSize().padding(padding).imePadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                OutlinedTextField(
                    f.title, { f.title = it }, label = { Text("Title") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("All day", modifier = Modifier.weight(1f))
                    Switch(f.allDay, { f.allDay = it })
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Starts", modifier = Modifier.weight(1f))
                    TextButton(onClick = { picking = "startDate" }) { Text(formats.shortDay(f.startDate)) }
                    if (!f.allDay) TextButton(onClick = { picking = "startTime" }) { Text(formats.timeOf(f.startTime)) }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Ends", modifier = Modifier.weight(1f))
                    TextButton(onClick = { picking = "endDate" }) { Text(formats.shortDay(f.endDate)) }
                    if (!f.allDay) TextButton(onClick = { picking = "endTime" }) { Text(formats.timeOf(f.endTime)) }
                }
                if (!viewModel.isSeriesPart) RepeatField(f, formats, onPickUntil = { picking = "until" })
                HorizontalDivider()
                if (target !is EditTarget.Existing || target.scope != Scope.ONE) {
                    Choice("Calendar", viewModel.calendars.firstOrNull { it.id == f.calendarId }?.name ?: "—", viewModel.calendars.map { it.id to it.name }) { f.calendarId = it }
                }
                val accountId = viewModel.calendars.firstOrNull { it.id == f.calendarId }?.accountId
                val categories = viewModel.categories.filter { it.accountId == accountId }
                if (categories.isNotEmpty()) {
                    Choice("Category", categories.firstOrNull { it.id == f.categoryId }?.name ?: "None", listOf<Pair<String?, String>>(null to "None") + categories.map { it.id to it.name }) { f.categoryId = it }
                }
                OutlinedTextField(f.location, { f.location = it }, label = { Text("Location") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                RemindersField(f)
                OutlinedTextField(f.notes, { f.notes = it }, label = { Text("Notes") }, minLines = 3, modifier = Modifier.fillMaxWidth())
                viewModel.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                Text(" ")
            }
        }
    }
    when (picking) {
        "startDate" -> PickDate(f.startDate, { picking = null }) { f.moveStart(it, f.startTime) }
        "endDate" -> PickDate(f.endDate, { picking = null }) { f.endDate = it }
        "until" -> PickDate(f.repeat?.until ?: f.startDate.plusMonths(1), { picking = null }) { f.repeat = f.repeat?.copy(until = it, count = null) }
        "startTime" -> PickTime(f.startTime, formats.is24h, { picking = null }) { f.moveStart(f.startDate, it) }
        "endTime" -> PickTime(f.endTime, formats.is24h, { picking = null }) { f.endTime = it }
    }
}

@Composable
private fun <T> Choice(label: String, shown: String, options: List<Pair<T, String>>, onPick: (T) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().clickable { open = true }.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Text(shown, color = MaterialTheme.colorScheme.primary)
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEach { (value, name) -> DropdownMenuItem(text = { Text(name) }, onClick = { open = false; onPick(value) }) }
        }
    }
}

@Composable
private fun RepeatField(f: EventForm, formats: Formats, onPickUntil: () -> Unit) {
    val weekday = f.startDate.dayOfWeek
    val presets: List<Pair<RepeatRule?, String>> = listOf(
        null to "Doesn't repeat",
        RepeatRule(RepeatRule.Freq.DAILY) to "Every day",
        RepeatRule(RepeatRule.Freq.WEEKLY, days = setOf(weekday)) to "Every week on ${formats.dayName(weekday)}",
        RepeatRule(RepeatRule.Freq.WEEKLY, days = DayOfWeek.entries.take(5).toSet()) to "Every weekday (Mon–Fri)",
        RepeatRule(RepeatRule.Freq.MONTHLY) to "Every month on day ${f.startDate.dayOfMonth}",
        RepeatRule(RepeatRule.Freq.YEARLY) to "Every year",
    )
    val shown = when {
        f.keptRule != null -> describeRule(f.keptRule!!)
        f.repeat == null -> "Doesn't repeat"
        else -> describeRule(f.repeat!!.format(f.allDay, ZoneOffset.UTC).substringBefore(";UNTIL"))
    }
    Choice("Repeat", shown, presets) { f.repeat = it?.copy(count = f.repeat?.count, until = f.repeat?.until); f.keptRule = null }
    val r = f.repeat ?: return
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text("Every", modifier = Modifier.padding(end = 8.dp))
        OutlinedTextField(
            r.interval.toString(), { v -> v.toIntOrNull()?.takeIf { it in 1..99 }?.let { f.repeat = r.copy(interval = it) } },
            singleLine = true, modifier = Modifier.weight(0.3f),
            keyboardOptions = KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Number),
        )
        Text(
            when (r.freq) { RepeatRule.Freq.DAILY -> "day(s)"; RepeatRule.Freq.WEEKLY -> "week(s)"; RepeatRule.Freq.MONTHLY -> "month(s)"; RepeatRule.Freq.YEARLY -> "year(s)" },
            modifier = Modifier.weight(0.7f).padding(start = 8.dp),
        )
    }
    if (r.freq == RepeatRule.Freq.WEEKLY) {
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            DayOfWeek.entries.forEach { d ->
                FilterChip(
                    selected = d in r.days,
                    onClick = { f.repeat = r.copy(days = if (d in r.days) r.days - d else r.days + d) },
                    label = { Text(d.getDisplayName(TextStyle.NARROW, formats.locale)) },
                )
            }
        }
    }
    val ends = when { r.count != null -> "After ${r.count} times"; r.until != null -> "On ${formats.shortDay(r.until)}"; else -> "Never" }
    Choice("Ends", ends, listOf("never" to "Never", "count" to "After a number of times", "until" to "On a date")) {
        when (it) {
            "never" -> f.repeat = r.copy(count = null, until = null)
            "count" -> f.repeat = r.copy(count = r.count ?: 10, until = null)
            else -> onPickUntil()
        }
    }
    if (r.count != null) {
        OutlinedTextField(
            r.count.toString(), { v -> v.toIntOrNull()?.takeIf { it in 1..999 }?.let { f.repeat = r.copy(count = it) } },
            label = { Text("Times") }, singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Number),
        )
    }
}

@Composable
private fun RemindersField(f: EventForm) {
    var adding by remember { mutableStateOf(false) }
    Column {
        Text("Reminders", style = MaterialTheme.typography.labelLarge)
        f.reminders.forEachIndexed { i, r ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(reminderText(r.minutesBefore, f.allDay), modifier = Modifier.weight(1f))
                TextButton(onClick = {
                    f.reminders = f.reminders.toMutableList().also { it[i] = r.copy(channel = if (r.channel == "Email") "WebPush" else "Email") }
                }) { Text(if (r.channel == "Email") "Email" else "Notification") }
                IconButton(onClick = { f.reminders = f.reminders.filterIndexed { j, _ -> j != i } }) { Icon(Icons.Default.Close, contentDescription = "Remove reminder") }
            }
        }
        Row {
            TextButton(onClick = { adding = true }) { Text("Add reminder") }
            DropdownMenu(expanded = adding, onDismissRequest = { adding = false }) {
                REMINDER_CHOICES.forEach { m ->
                    DropdownMenuItem(text = { Text(reminderText(m, f.allDay)) }, onClick = {
                        adding = false
                        f.reminders = (f.reminders + Reminder(m, "WebPush")).distinct().sortedBy { it.minutesBefore }
                    })
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PickDate(initial: LocalDate, onDone: () -> Unit, onPick: (LocalDate) -> Unit) {
    val state = rememberDatePickerState(initialSelectedDateMillis = initial.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli())
    DatePickerDialog(
        onDismissRequest = onDone,
        confirmButton = {
            TextButton(onClick = {
                state.selectedDateMillis?.let { onPick(java.time.Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate()) }
                onDone()
            }) { Text("OK") }
        },
        dismissButton = { TextButton(onClick = onDone) { Text("Cancel") } },
    ) { DatePicker(state) }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PickTime(initial: LocalTime, is24h: Boolean, onDone: () -> Unit, onPick: (LocalTime) -> Unit) {
    val state = rememberTimePickerState(initial.hour, initial.minute, is24h)
    AlertDialog(
        onDismissRequest = onDone,
        confirmButton = { TextButton(onClick = { onPick(LocalTime.of(state.hour, state.minute)); onDone() }) { Text("OK") } },
        dismissButton = { TextButton(onClick = onDone) { Text("Cancel") } },
        text = { TimePicker(state) },
    )
}
