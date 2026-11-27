package org.hyperstarit.twentyfourseven.ui.tasks

import org.hyperstarit.twentyfourseven.data.db.scopedKey
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import org.hyperstarit.twentyfourseven.ui.calendar.parseColor
import org.hyperstarit.twentyfourseven.ui.editor.ChoiceChip
import org.hyperstarit.twentyfourseven.ui.editor.EditorScaffold
import org.hyperstarit.twentyfourseven.ui.editor.Gap
import org.hyperstarit.twentyfourseven.ui.editor.InlineField
import org.hyperstarit.twentyfourseven.ui.editor.LabeledLine
import org.hyperstarit.twentyfourseven.ui.editor.PickDate
import org.hyperstarit.twentyfourseven.ui.editor.PickTime
import org.hyperstarit.twentyfourseven.ui.theme.CheckCircle
import org.hyperstarit.twentyfourseven.ui.theme.EmptyState
import org.hyperstarit.twentyfourseven.ui.theme.FormRow
import org.hyperstarit.twentyfourseven.ui.theme.GhostButton
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.Segmented
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import org.hyperstarit.twentyfourseven.ui.theme.TopBar
import org.hyperstarit.twentyfourseven.ui.theme.ValueChip
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import javax.inject.Inject
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.calendar.Recurrence
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.TaskData
import org.hyperstarit.twentyfourseven.sync.LocalChanges
import org.hyperstarit.twentyfourseven.ui.calendar.Formats
import org.hyperstarit.twentyfourseven.ui.calendar.rememberFormats

private val OPEN = setOf("NeedsAction", "InProcess")

data class TaskTree(val task: TaskData, val subtasks: List<TaskData>)

data class TasksState(val open: List<TaskTree> = emptyList(), val done: List<TaskData> = emptyList(), val calendars: List<CalendarEntity> = emptyList())

@HiltViewModel
class TasksViewModel @Inject constructor(db: AppDatabase, private val changes: LocalChanges) : ViewModel() {
    val state = combine(db.tasks().observeLive(), db.calendars().observeAll()) { rows, calendars ->
        val writable = calendars.filter { !it.readOnly && it.allowsTasks }
        val all = rows.map { it.data }.filter { t -> calendars.any { it.accountId == t.accountId && it.id == t.calendarId } }
        val children = all.filter { it.parentId != null }.groupBy { scopedKey(it.accountId, it.parentId!!) }
        val order = compareBy<TaskData>({ it.due == null }, { it.due }, { it.sortOrder }, { it.title.lowercase() })
        TasksState(
            open = all.filter { it.parentId == null && it.status in OPEN }.sortedWith(order)
                .map { TaskTree(it, children[scopedKey(it.accountId, it.id)].orEmpty().sortedWith(compareBy({ it.status !in OPEN }, { it.sortOrder }, { it.title }))) },
            done = all.filter { it.parentId == null && it.status !in OPEN }.sortedByDescending { it.completedAt ?: 0 },
            calendars = writable,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), TasksState())

    fun toggle(t: TaskData) = viewModelScope.launch { changes.setTaskDone(t.accountId, t.id, t.status in OPEN) }

    fun save(t: TaskData) = viewModelScope.launch { if (t.version == null && t.id.isEmpty()) changes.createTask(t) else changes.updateTask(t) }

    fun delete(t: TaskData) = viewModelScope.launch { changes.deleteTask(t.accountId, t.id) }
}

/** The tasks of every account, grouped by when they're due, then the done ones. */
@Composable
fun TasksScreen(bottomBar: @Composable () -> Unit, viewModel: TasksViewModel = hiltViewModel()) {
    val c = Brand.colors
    val state by viewModel.state.collectAsStateWithLifecycle()
    val formats = rememberFormats()
    var editing by remember { mutableStateOf<TaskData?>(null) }
    var showDone by remember { mutableStateOf(false) }
    val today = LocalDate.now(formats.zone)
    val groups = state.open.groupBy { tree ->
        val t = tree.task
        when {
            t.due == null -> 3
            overdue(t, formats) -> 0
            dueDate(t, formats) == today -> 1
            else -> 2
        }
    }
    Scaffold(
        containerColor = Color.Transparent,
        bottomBar = bottomBar,
        topBar = {
            TopBar("Tasks", subtitle = when (state.open.size) { 0 -> "All done"; 1 -> "1 open"; else -> "${state.open.size} open" })
        },
        floatingActionButton = {
            state.calendars.firstOrNull()?.let { calendar ->
                Box(
                    Modifier.size(58.dp)
                        .shadow(16.dp, RoundedCornerShape(18.dp), ambientColor = c.highlight, spotColor = c.highlight.copy(alpha = 0.7f * c.glow))
                        .clip(RoundedCornerShape(18.dp)).background(c.grad)
                        .clickable { editing = blankTask(calendar, null) },
                    contentAlignment = Alignment.Center,
                ) { Icon(Glyphs.plus, "New task", tint = c.onAccent, modifier = Modifier.size(26.dp)) }
            }
        },
    ) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding), contentPadding = PaddingValues(start = 12.dp, end = 12.dp, top = 4.dp, bottom = 96.dp)) {
            if (state.open.isEmpty()) {
                item { EmptyState(Glyphs.tasks, "Nothing to do", "Tap + to add a task. Tasks sync with your calendars, on the web and on your phone.", Modifier.padding(top = 40.dp)) }
            }
            listOf(0 to "Overdue", 1 to "Today", 2 to "Coming up", 3 to "Someday").forEach { (group, label) ->
                val trees = groups[group].orEmpty()
                if (trees.isNotEmpty()) {
                    item(key = "g$group") {
                        SectionLabel("$label · ${trees.size}", Modifier.padding(start = 8.dp, top = 16.dp, bottom = 8.dp), color = if (group == 0) c.dangerInk else c.textFaint)
                    }
                    items(trees, key = { scopedKey(it.task.accountId, it.task.id) }) { tree ->
                        TaskCard(tree, state.calendars, formats, onToggle = viewModel::toggle, onOpen = { editing = it })
                    }
                }
            }
            if (state.done.isNotEmpty()) {
                item(key = "done") {
                    Row(
                        Modifier.fillMaxWidth().padding(top = 18.dp).clip(RoundedCornerShape(12.dp)).clickable { showDone = !showDone }.padding(horizontal = 8.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        SectionLabel("Done · ${state.done.size}", Modifier.weight(1f))
                        Icon(Glyphs.chevronDown, if (showDone) "Hide done" else "Show done", tint = c.textFaint, modifier = Modifier.size(18.dp).rotate(if (showDone) 180f else 0f))
                    }
                }
                if (showDone) items(state.done, key = { "d" + scopedKey(it.accountId, it.id) }) { TaskCard(TaskTree(it, emptyList()), state.calendars, formats, viewModel::toggle) { t -> editing = t } }
            }
        }
    }
    editing?.let { task ->
        TaskEditor(
            task, state.calendars, formats,
            subtasks = state.open.firstOrNull { it.task.accountId == task.accountId && it.task.id == task.id }?.subtasks.orEmpty(),
            onSave = { viewModel.save(it); editing = null },
            onDelete = { viewModel.delete(task); editing = null },
            onAddSubtask = { title -> viewModel.save(blankTask(state.calendars.first { it.accountId == task.accountId && it.id == task.calendarId }, task.id).copy(title = title)) },
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
    val date = dueDate(t, formats)!!
    val today = LocalDate.now(formats.zone)
    val day = when (date) {
        today -> "Today"
        today.plusDays(1) -> "Tomorrow"
        today.minusDays(1) -> "Yesterday"
        else -> formats.shortDay(date)
    }
    if (t.dueAllDay) day else "$day · ${formats.time(due)}"
}

private fun dueDate(t: TaskData, formats: Formats): LocalDate? = t.due?.let { due ->
    if (t.dueAllDay) Recurrence.dateOf(due) else Instant.ofEpochMilli(due).atZone(formats.zone).toLocalDate()
}

private fun overdue(t: TaskData, formats: Formats): Boolean = t.status in OPEN && t.due?.let { due ->
    if (t.dueAllDay) Recurrence.dateOf(due).isBefore(LocalDate.now(formats.zone)) else due < System.currentTimeMillis()
} == true

@Composable
private fun priorityColor(priority: String?): Color? = when (priority) {
    "high" -> Brand.colors.danger
    "normal" -> Brand.colors.accent
    "low" -> Brand.colors.textFaint
    else -> null
}

/** A task as a card: its check, title, due and priority, and its subtasks beneath. */
@Composable
private fun TaskCard(tree: TaskTree, calendars: List<CalendarEntity>, formats: Formats, onToggle: (TaskData) -> Unit, onOpen: (TaskData) -> Unit) {
    val c = Brand.colors
    val t = tree.task
    val done = t.status !in OPEN
    val ring = parseColor(calendars.firstOrNull { it.accountId == t.accountId && it.id == t.calendarId }?.color) ?: c.hairlineStrong
    Column(
        Modifier.fillMaxWidth().padding(vertical = 3.dp).clip(RoundedCornerShape(16.dp)).background(c.surface)
            .border(1.dp, c.hairline, RoundedCornerShape(16.dp)),
    ) {
        Row(Modifier.fillMaxWidth().clickable { onOpen(t) }.padding(start = 4.dp, end = 14.dp, top = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            CheckCircle(done, { onToggle(t) }, ring = ring.copy(alpha = 0.8f))
            Column(Modifier.weight(1f).padding(vertical = 8.dp)) {
                Text(
                    t.title, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium,
                    textDecoration = if (done) TextDecoration.LineThrough else null,
                    color = if (done) c.textFaint else c.text,
                )
                val due = dueText(t, formats)
                val subs = tree.subtasks.size
                if (due != null || subs > 0) {
                    Row(Modifier.padding(top = 3.dp), verticalAlignment = Alignment.CenterVertically) {
                        if (due != null) {
                            val late = overdue(t, formats)
                            Icon(Glyphs.clock, null, tint = if (late) c.dangerInk else c.textFaint, modifier = Modifier.size(13.dp))
                            Text(due, style = MaterialTheme.typography.bodySmall, color = if (late) c.dangerInk else c.textDim, modifier = Modifier.padding(start = 4.dp, end = 10.dp))
                        }
                        if (subs > 0) {
                            Icon(Glyphs.tasks, null, tint = c.textFaint, modifier = Modifier.size(13.dp))
                            Text(
                                "${tree.subtasks.count { it.status !in OPEN }}/$subs", style = Brand.data, fontSize = 11.sp, color = c.textDim,
                                modifier = Modifier.padding(start = 4.dp),
                            )
                        }
                    }
                }
            }
            priorityColor(t.priority)?.let { Icon(Glyphs.flag, "Priority ${t.priority}", tint = it, modifier = Modifier.size(16.dp)) }
        }
        tree.subtasks.forEach { s ->
            Hairline(Modifier.padding(start = 44.dp))
            Row(Modifier.fillMaxWidth().clickable { onOpen(s) }.padding(start = 36.dp, end = 14.dp), verticalAlignment = Alignment.CenterVertically) {
                CheckCircle(s.status !in OPEN, { onToggle(s) }, size = 20.dp)
                Text(
                    s.title, style = MaterialTheme.typography.bodyMedium,
                    textDecoration = if (s.status !in OPEN) TextDecoration.LineThrough else null,
                    color = if (s.status !in OPEN) c.textFaint else c.text, modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

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
    val c = Brand.colors
    var title by remember { mutableStateOf(initial.title) }
    var notes by remember { mutableStateOf(initial.notes.orEmpty()) }
    var priority by remember { mutableStateOf(initial.priority) }
    // Calendar ids repeat across accounts: the picker works with scoped keys.
    var calendarKey by remember { mutableStateOf(scopedKey(initial.accountId, initial.calendarId)) }
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
        val calendar = calendars.firstOrNull { scopedKey(it.accountId, it.id) == calendarKey }
        return initial.copy(
            title = title.trim(), notes = notes.ifBlank { null }, priority = priority, calendarId = calendar?.id ?: initial.calendarId,
            accountId = calendar?.accountId ?: initial.accountId, due = due, dueAllDay = date != null && dueTime == null,
            timeZone = if (date != null && dueTime != null) formats.zone.id else initial.timeZone,
        )
    }

    EditorScaffold(
        if (isNew) (if (initial.parentId != null) "New subtask" else "New task") else "Edit task",
        onClose, onSave = { onSave(result()) }, saveEnabled = title.isNotBlank(),
    ) {
        InlineField(title, { title = it }, "What needs doing?", Modifier.padding(start = 8.dp, bottom = 12.dp), style = MaterialTheme.typography.headlineSmall)
        Panel(padding = 6.dp) {
            FormRow(Glyphs.clock) {
                LabeledLine("Due") {
                    ValueChip(dueDate?.let(formats::shortDay) ?: "No date", { picking = "date" })
                    if (dueDate != null) {
                        Gap()
                        ValueChip(dueTime?.let(formats::timeOf) ?: "Any time", { picking = "time" }, mono = dueTime != null)
                        Box(Modifier.padding(start = 2.dp).size(36.dp).clip(CircleShape).clickable { dueDate = null; dueTime = null }, contentAlignment = Alignment.Center) {
                            Icon(Glyphs.close, "No due date", tint = c.textFaint, modifier = Modifier.size(18.dp))
                        }
                    }
                }
            }
            FormRow(Glyphs.flag, iconTint = priorityColor(priority) ?: c.textFaint) {
                Segmented(
                    listOf<Pair<String?, String>>(null to "None", "low" to "Low", "normal" to "Normal", "high" to "High"),
                    priority, { priority = it }, Modifier.fillMaxWidth(),
                )
            }
            // Subtasks live in their parent's calendar.
            if (initial.parentId == null && subtasks.isEmpty()) {
                FormRow(Glyphs.calendar) {
                    LabeledLine("Calendar") {
                        ChoiceChip(
                            calendars.firstOrNull { scopedKey(it.accountId, it.id) == calendarKey }?.name ?: "—",
                            calendars.map { scopedKey(it.accountId, it.id) to it.name }, { calendarKey = it },
                            dots = calendars.associate { scopedKey(it.accountId, it.id) to (parseColor(it.color) ?: c.accent) },
                        )
                    }
                }
            }
        }
        Spacer(Modifier.height(12.dp))
        Panel(padding = 6.dp) {
            FormRow(Glyphs.text) { InlineField(notes, { notes = it }, "Add notes", singleLine = false, minLines = 3) }
        }
        if (!isNew && initial.parentId == null) {
            SectionLabel("Subtasks", Modifier.padding(start = 8.dp, top = 20.dp, bottom = 8.dp))
            Panel(padding = 6.dp) {
                subtasks.forEach { s ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        CheckCircle(s.status !in OPEN, { onToggle(s) }, size = 20.dp)
                        Text(
                            s.title, textDecoration = if (s.status !in OPEN) TextDecoration.LineThrough else null,
                            color = if (s.status !in OPEN) c.textFaint else c.text, modifier = Modifier.weight(1f),
                        )
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(start = 10.dp)) {
                    Icon(Glyphs.plus, null, tint = c.textFaint, modifier = Modifier.size(18.dp))
                    InlineField(newSubtask, { newSubtask = it }, "Add a subtask", Modifier.weight(1f).padding(start = 14.dp))
                    if (newSubtask.isNotBlank()) TextAction("Add", { onAddSubtask(newSubtask.trim()); newSubtask = "" })
                }
            }
        }
        if (!isNew) {
            GhostButton("Delete task", onDelete, Modifier.padding(top = 24.dp).fillMaxWidth(), icon = Glyphs.trash, danger = true)
        }
    }
    when (picking) {
        "date" -> PickDate(dueDate ?: LocalDate.now(formats.zone), { picking = null }) { dueDate = it }
        "time" -> PickTime(
            dueTime ?: LocalTime.of(9, 0), formats.is24h, { picking = null }, dismissLabel = "Any time",
            onDismissAction = { dueTime = null; picking = null },
        ) { dueTime = it }
    }
}
