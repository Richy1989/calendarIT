package org.hyperstarit.planit.ui.tasks

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
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
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneOffset
import javax.inject.Inject
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.hyperstarit.planit.calendar.Recurrence
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.data.db.TaskData
import org.hyperstarit.planit.sync.LocalChanges
import org.hyperstarit.planit.ui.calendar.Formats
import org.hyperstarit.planit.ui.calendar.rememberFormats

private val OPEN = setOf("NeedsAction", "InProcess")

data class TaskTree(val task: TaskData, val subtasks: List<TaskData>)

data class TasksState(val open: List<TaskTree> = emptyList(), val done: List<TaskData> = emptyList(), val calendars: List<CalendarEntity> = emptyList())

@HiltViewModel
class TasksViewModel @Inject constructor(db: PlanItDatabase, private val changes: LocalChanges) : ViewModel() {
    val state = combine(db.tasks().observeLive(), db.calendars().observeAll()) { rows, calendars ->
        val writable = calendars.filter { !it.readOnly }
        val all = rows.map { it.data }.filter { t -> calendars.any { it.id == t.calendarId } }
        val children = all.filter { it.parentId != null }.groupBy { it.parentId }
        val order = compareBy<TaskData>({ it.due == null }, { it.due }, { it.sortOrder }, { it.title.lowercase() })
        TasksState(
            open = all.filter { it.parentId == null && it.status in OPEN }.sortedWith(order)
                .map { TaskTree(it, children[it.id].orEmpty().sortedWith(compareBy({ it.status !in OPEN }, { it.sortOrder }, { it.title }))) },
            done = all.filter { it.parentId == null && it.status !in OPEN }.sortedByDescending { it.completedAt ?: 0 },
            calendars = writable,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), TasksState())

    fun toggle(t: TaskData) = viewModelScope.launch { changes.setTaskDone(t.id, t.status in OPEN) }

    fun save(t: TaskData) = viewModelScope.launch { if (t.version == null && t.id.isEmpty()) changes.createTask(t) else changes.updateTask(t) }

    fun delete(t: TaskData) = viewModelScope.launch { changes.deleteTask(t.id) }
}

/** The tasks of every account: open ones with their checklists, then the done ones. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TasksScreen(onBack: () -> Unit, viewModel: TasksViewModel = hiltViewModel()) {
    BackHandler(onBack = onBack)
    val state by viewModel.state.collectAsStateWithLifecycle()
    val formats = rememberFormats()
    var editing by remember { mutableStateOf<TaskData?>(null) }
    var showDone by remember { mutableStateOf(false) }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Tasks") },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back") } },
            )
        },
        floatingActionButton = {
            state.calendars.firstOrNull()?.let { calendar ->
                FloatingActionButton(onClick = { editing = blankTask(calendar, null) }) { Icon(Icons.Default.Add, contentDescription = "New task") }
            }
        },
    ) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            if (state.open.isEmpty()) item { Text("Nothing to do.", modifier = Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }
            state.open.forEach { tree ->
                item(key = tree.task.id) { TaskRow(tree.task, formats, indent = false, onToggle = viewModel::toggle, onOpen = { editing = it }) }
                items(tree.subtasks, key = { it.id }) { TaskRow(it, formats, indent = true, onToggle = viewModel::toggle, onOpen = { editing = it }) }
            }
            if (state.done.isNotEmpty()) {
                item(key = "done") {
                    TextButton(onClick = { showDone = !showDone }, modifier = Modifier.padding(horizontal = 8.dp)) {
                        Text(if (showDone) "Hide done (${state.done.size})" else "Show done (${state.done.size})")
                    }
                }
                if (showDone) items(state.done, key = { "d" + it.id }) { TaskRow(it, formats, indent = false, onToggle = viewModel::toggle, onOpen = { editing = it }) }
            }
        }
    }
    editing?.let { task ->
        TaskEditor(
            task, state.calendars, formats,
            subtasks = state.open.firstOrNull { it.task.id == task.id }?.subtasks.orEmpty(),
            onSave = { viewModel.save(it); editing = null },
            onDelete = { viewModel.delete(task); editing = null },
            onAddSubtask = { title -> viewModel.save(blankTask(state.calendars.first { it.id == task.calendarId }, task.id).copy(title = title)) },
            onToggle = viewModel::toggle,
            onClose = { editing = null },
        )
    }
}

private fun blankTask(calendar: CalendarEntity, parentId: String?) = TaskData(
    id = "", accountId = calendar.accountId, calendarId = calendar.id, title = "", notes = null, due = null, dueAllDay = false,
    timeZone = null, status = "NeedsAction", completedAt = null, priority = null, parentId = parentId, sortOrder = 0,
    reminders = emptyList(), version = null,
)

/** When it is due, in words; null when it has no date. */
private fun dueText(t: TaskData, formats: Formats): String? = t.due?.let { due ->
    if (t.dueAllDay) formats.shortDay(Recurrence.dateOf(due))
    else "${formats.shortDay(Instant.ofEpochMilli(due).atZone(formats.zone).toLocalDate())} ${formats.time(due)}"
}

private fun overdue(t: TaskData, formats: Formats): Boolean = t.status in OPEN && t.due?.let { due ->
    if (t.dueAllDay) Recurrence.dateOf(due).isBefore(LocalDate.now(formats.zone)) else due < System.currentTimeMillis()
} == true

@Composable
private fun TaskRow(t: TaskData, formats: Formats, indent: Boolean, onToggle: (TaskData) -> Unit, onOpen: (TaskData) -> Unit) {
    val done = t.status !in OPEN
    Row(
        Modifier.fillMaxWidth().clickable { onOpen(t) }.padding(start = if (indent) 40.dp else 8.dp, end = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Checkbox(checked = done, onCheckedChange = { onToggle(t) })
        Column(Modifier.weight(1f)) {
            Text(
                (if (t.priority == "high") "! " else "") + t.title,
                textDecoration = if (done) TextDecoration.LineThrough else null,
                color = if (done) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
            )
            dueText(t, formats)?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = if (overdue(t, formats)) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TaskEditor(
    initial: TaskData,
    calendars: List<CalendarEntity>,
    formats: Formats,
    subtasks: List<TaskData>,
    onSave: (TaskData) -> Unit,
    onDelete: () -> Unit,
    onAddSubtask: (String) -> Unit,
    onToggle: (TaskData) -> Unit,
    onClose: () -> Unit,
) {
    var title by remember { mutableStateOf(initial.title) }
    var notes by remember { mutableStateOf(initial.notes.orEmpty()) }
    var priority by remember { mutableStateOf(initial.priority) }
    var calendarId by remember { mutableStateOf(initial.calendarId) }
    var dueDate by remember { mutableStateOf(initial.due?.let { if (initial.dueAllDay) Recurrence.dateOf(it) else Instant.ofEpochMilli(it).atZone(formats.zone).toLocalDate() }) }
    var dueTime by remember { mutableStateOf(initial.due?.takeIf { !initial.dueAllDay }?.let { Instant.ofEpochMilli(it).atZone(formats.zone).toLocalTime().withSecond(0).withNano(0) }) }
    var newSubtask by remember { mutableStateOf("") }
    var picking by remember { mutableStateOf<String?>(null) }
    val isNew = initial.id.isEmpty()

    fun result(): TaskData {
        val date = dueDate
        val due = when {
            date == null -> null
            dueTime == null -> Recurrence.dayMillis(date)
            else -> date.atTime(dueTime).atZone(formats.zone).toInstant().toEpochMilli()
        }
        val calendar = calendars.firstOrNull { it.id == calendarId }
        return initial.copy(
            title = title.trim(), notes = notes.ifBlank { null }, priority = priority, calendarId = calendarId,
            accountId = calendar?.accountId ?: initial.accountId, due = due, dueAllDay = date != null && dueTime == null,
            timeZone = if (date != null && dueTime != null) formats.zone.id else initial.timeZone,
        )
    }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Scaffold(
            topBar = {
                TopAppBar(
                    navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.Default.Close, contentDescription = "Cancel") } },
                    title = { Text(if (isNew) (if (initial.parentId != null) "New subtask" else "New task") else "Edit task") },
                    actions = { TextButton(onClick = { onSave(result()) }, enabled = title.isNotBlank()) { Text("Save") } },
                )
            },
        ) { padding ->
            Column(
                Modifier.fillMaxSize().padding(padding).imePadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                OutlinedTextField(
                    title, { title = it }, label = { Text("Title") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Due", modifier = Modifier.weight(1f))
                    TextButton(onClick = { picking = "date" }) { Text(dueDate?.let(formats::shortDay) ?: "No date") }
                    if (dueDate != null) {
                        TextButton(onClick = { picking = "time" }) { Text(dueTime?.let(formats::timeOf) ?: "Any time") }
                        IconButton(onClick = { dueDate = null; dueTime = null }) { Icon(Icons.Default.Close, contentDescription = "No due date") }
                    }
                }
                Pick("Priority", when (priority) { "high" -> "High"; "normal" -> "Normal"; "low" -> "Low"; else -> "None" },
                    listOf(null to "None", "high" to "High", "normal" to "Normal", "low" to "Low")) { priority = it }
                // Subtasks live in their parent's calendar.
                if (initial.parentId == null && subtasks.isEmpty()) {
                    Pick("Calendar", calendars.firstOrNull { it.id == calendarId }?.name ?: "—", calendars.map { it.id to it.name }) { calendarId = it }
                }
                OutlinedTextField(notes, { notes = it }, label = { Text("Notes") }, minLines = 3, modifier = Modifier.fillMaxWidth())
                if (!isNew && initial.parentId == null) {
                    Text("Subtasks", style = MaterialTheme.typography.labelLarge)
                    subtasks.forEach { s ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = s.status !in OPEN, onCheckedChange = { onToggle(s) })
                            Text(s.title, textDecoration = if (s.status !in OPEN) TextDecoration.LineThrough else null)
                        }
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(newSubtask, { newSubtask = it }, label = { Text("Add a subtask") }, singleLine = true, modifier = Modifier.weight(1f))
                        IconButton(enabled = newSubtask.isNotBlank(), onClick = { onAddSubtask(newSubtask.trim()); newSubtask = "" }) {
                            Icon(Icons.Default.Add, contentDescription = "Add subtask")
                        }
                    }
                }
                if (!isNew) OutlinedButton(onClick = onDelete) { Text("Delete task") }
            }
        }
    }
    when (picking) {
        "date" -> {
            val state = rememberDatePickerState(initialSelectedDateMillis = (dueDate ?: LocalDate.now(formats.zone)).atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli())
            DatePickerDialog(
                onDismissRequest = { picking = null },
                confirmButton = {
                    TextButton(onClick = {
                        state.selectedDateMillis?.let { dueDate = Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate() }
                        picking = null
                    }) { Text("OK") }
                },
                dismissButton = { TextButton(onClick = { picking = null }) { Text("Cancel") } },
            ) { DatePicker(state) }
        }
        "time" -> {
            val t = dueTime ?: LocalTime.of(9, 0)
            val state = rememberTimePickerState(t.hour, t.minute, formats.is24h)
            AlertDialog(
                onDismissRequest = { picking = null },
                confirmButton = { TextButton(onClick = { dueTime = LocalTime.of(state.hour, state.minute); picking = null }) { Text("OK") } },
                dismissButton = { TextButton(onClick = { dueTime = null; picking = null }) { Text("Any time") } },
                text = { TimePicker(state) },
            )
        }
    }
}

@Composable
private fun <T> Pick(label: String, shown: String, options: List<Pair<T, String>>, onPick: (T) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().clickable { open = true }.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Text(shown, color = MaterialTheme.colorScheme.primary)
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEach { (v, name) -> DropdownMenuItem(text = { Text(name) }, onClick = { open = false; onPick(v) }) }
        }
    }
}
