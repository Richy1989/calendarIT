package org.hyperstarit.twentyfourseven.ui.editor

import org.hyperstarit.twentyfourseven.data.db.scopedKey
import androidx.compose.foundation.clickable
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import org.hyperstarit.twentyfourseven.ui.calendar.parseColor
import org.hyperstarit.twentyfourseven.ui.theme.Banner
import org.hyperstarit.twentyfourseven.ui.theme.FormRow
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.BrandSwitch
import org.hyperstarit.twentyfourseven.ui.theme.ValueChip
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.DayOfWeek
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.TextStyle
import javax.inject.Inject
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.calendar.RepeatRule
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.CategoryEntity
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.hyperstarit.twentyfourseven.sync.LocalChanges
import org.hyperstarit.twentyfourseven.ui.calendar.Formats
import org.hyperstarit.twentyfourseven.ui.calendar.describeRule
import org.hyperstarit.twentyfourseven.ui.calendar.reminderText

@HiltViewModel
class EventEditorViewModel @Inject constructor(
    private val db: AppDatabase,
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
            val all = db.calendars().observeAll().first().filter { !it.readOnly && it.allowsEvents }
            categories = db.categories().observeAll().first()
            when (target) {
                is EditTarget.New -> {
                    val calendar = all.firstOrNull()
                    calendars = all
                    form.calendarId = calendar?.id
                    form.accountId = calendar?.accountId
                    val time = target.time ?: LocalTime.of(9, 0)
                    form.startDate = target.date; form.startTime = time
                    val end = target.date.atTime(time).plusHours(1)
                    form.endDate = end.toLocalDate(); form.endTime = end.toLocalTime()
                    // A new timed event starts with its calendar's default reminders, as on the web.
                    form.reminders = calendar?.defaultReminders.orEmpty()
                }
                is EditTarget.Existing -> {
                    val o = target.occurrence
                    val master = db.events().live(o.accountId, o.seriesId ?: o.eventId)?.data ?: return@launch
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
        else (db.events().overrideEdits(master.accountId, master.id).map { it.data } + db.events().serverOverrides(master.accountId, master.id).map { it.data })
            .firstOrNull { it.id == eventId }?.categoryId ?: master.categoryId

    val isSeriesPart get() = (target as? EditTarget.Existing)?.scope.let { it == Scope.ONE }

    fun save(done: () -> Unit) {
        form.error()?.let { error = it; return }
        val t = target ?: return
        viewModelScope.launch {
            when (t) {
                is EditTarget.New -> {
                    val calendar = calendars.first { it.accountId == form.accountId && it.id == form.calendarId }
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
                        Scope.ONE -> changes.editOccurrence(master.accountId, master.id, o.recurrenceId!!, form.toEvent(master).copy(recurrence = null))
                        Scope.FOLLOWING -> changes.editFollowing(master.accountId, master.id, o.recurrenceId!!, form.toEvent(master))
                    }
                }
            }
            done()
        }
    }
}

private val REMINDER_CHOICES = listOf(0, 5, 10, 15, 30, 60, 120, 1440, 2880, 10080)

/** Creating or editing an appointment, in a compact sheet. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EventEditor(target: EditTarget, formats: Formats, onClose: () -> Unit, viewModel: EventEditorViewModel = hiltViewModel()) {
    val session = remember { Any() }
    viewModel.open(target, session)
    val f = viewModel.form
    val c = Brand.colors
    var picking by remember { mutableStateOf<String?>(null) } // "startDate", "startTime", "endDate", "endTime", "until"
    val kind = when {
        target is EditTarget.New -> "New appointment"
        (target as EditTarget.Existing).scope == Scope.ONE -> "Edit this occurrence"
        target.scope == Scope.FOLLOWING -> "Edit this and following"
        else -> "Edit appointment"
    }
    ModalBottomSheet(
        onDismissRequest = onClose,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = c.surface,
        dragHandle = null,
    ) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(kind, Modifier.weight(1f), style = MaterialTheme.typography.titleMedium, color = c.text)
            RoundIconButton(Glyphs.close, "Cancel", onClose)
        }
        var optionsOverride by rememberSaveable { mutableStateOf<Boolean?>(null) }
        val showOptions = optionsOverride ?: (target is EditTarget.Existing && (f.notes.isNotBlank() || f.reminders.isNotEmpty()))
        Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState()).padding(horizontal = 12.dp)) {
            val calendar = viewModel.calendars.firstOrNull { it.accountId == f.accountId && it.id == f.calendarId }
            val categories = viewModel.categories.filter { it.accountId == calendar?.accountId }
            val category = categories.firstOrNull { it.id == f.categoryId }
            FormRow(Glyphs.text) {
                InlineField(f.title, { f.title = it }, "Add title", style = MaterialTheme.typography.titleLarge)
            }
            Hairline()
            FormRow(Glyphs.clock) {
                ValueChip(formats.shortDay(f.startDate), { picking = "startDate" }, Modifier.weight(1.3f).semantics { contentDescription = "Start date" })
                if (!f.allDay) {
                    Gap()
                    ValueChip(formats.timeOf(f.startTime), { picking = "startTime" }, Modifier.weight(1f).semantics { contentDescription = "Start time" }, mono = true)
                }
            }
            FormRow(null) {
                ValueChip(formats.shortDay(f.endDate), { picking = "endDate" }, Modifier.weight(1.3f).semantics { contentDescription = "End date" })
                if (!f.allDay) {
                    Gap()
                    ValueChip(formats.timeOf(f.endTime), { picking = "endTime" }, Modifier.weight(1f).semantics { contentDescription = "End time" }, mono = true)
                }
            }
            FormRow(null) {
                LabeledLine("All day") { BrandSwitch(f.allDay, { f.allDay = it }) }
            }
            if (!viewModel.isSeriesPart) RepeatField(f, formats, onPickUntil = { picking = "until" })
            Hairline()
            if (target !is EditTarget.Existing || target.scope != Scope.ONE) {
                FormRow(Glyphs.calendar) {
                    ChoiceChip(
                        calendar?.name ?: "Calendar", viewModel.calendars.map { scopedKey(it.accountId, it.id) to it.name },
                        { key -> viewModel.calendars.firstOrNull { scopedKey(it.accountId, it.id) == key }?.let {
                            if (f.accountId != it.accountId) f.categoryId = null
                            f.accountId = it.accountId
                            f.calendarId = it.id
                        } },
                        modifier = Modifier.semantics { contentDescription = "Calendar" },
                        dots = viewModel.calendars.associate { scopedKey(it.accountId, it.id) to (parseColor(it.color) ?: c.accent) },
                        selected = calendar?.let { scopedKey(it.accountId, it.id) },
                    )
                }
            }
            if (categories.isNotEmpty()) {
                FormRow(Glyphs.tag) {
                    ChoiceChip(
                        category?.name ?: "Add category", listOf<Pair<String?, String>>(null to "None") + categories.map { it.id to it.name },
                        { f.categoryId = it }, modifier = Modifier.semantics { contentDescription = "Category" },
                        dots = categories.associate { it.id to (parseColor(it.color) ?: c.accent) }, selected = category?.id,
                    )
                }
            }
            FormRow(Glyphs.pin) { InlineField(f.location, { f.location = it }, "Add location") }
            if (showOptions) {
                FormRow(Glyphs.text) { InlineField(f.notes, { f.notes = it }, "Add notes", singleLine = false) }
                RemindersField(f)
            }
            viewModel.error?.let { Banner(it) }
        }
        Hairline()
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            TextAction(if (showOptions) "Fewer options" else "More options", { optionsOverride = !showOptions })
            Spacer(Modifier.weight(1f))
            PrimaryButton("Save", { viewModel.save(onClose) }, enabled = viewModel.ready)
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
private fun RepeatField(f: EventForm, formats: Formats, onPickUntil: () -> Unit) {
    val c = Brand.colors
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
    FormRow(Glyphs.repeat) {
        ChoiceChip(shown, presets, { f.repeat = it?.copy(count = f.repeat?.count, until = f.repeat?.until); f.keptRule = null })
    }
    val r = f.repeat ?: return
    FormRow(null) {
        LabeledLine("Every") {
            Stepper(r.interval, 1..99) { f.repeat = r.copy(interval = it) }
            Text(
                when (r.freq) {
                    RepeatRule.Freq.DAILY -> if (r.interval == 1) "day" else "days"
                    RepeatRule.Freq.WEEKLY -> if (r.interval == 1) "week" else "weeks"
                    RepeatRule.Freq.MONTHLY -> if (r.interval == 1) "month" else "months"
                    RepeatRule.Freq.YEARLY -> if (r.interval == 1) "year" else "years"
                },
                color = c.textDim, modifier = Modifier.padding(start = 10.dp).width(56.dp),
            )
        }
    }
    if (r.freq == RepeatRule.Freq.WEEKLY) {
        FormRow(null) {
            Row(horizontalArrangement = Arrangement.spacedBy(5.dp), modifier = Modifier.fillMaxWidth()) {
                // No days named = the start's weekday (as the server reads it), so show that one.
                val effective = r.days.ifEmpty { setOf(weekday) }
                DayOfWeek.entries.forEach { d ->
                    val on = d in effective
                    Box(
                        Modifier.weight(1f).aspectRatio(1f).clip(CircleShape)
                            .then(if (on) Modifier.background(c.grad) else Modifier.background(c.surface2).border(1.dp, c.hairline, CircleShape))
                            .clickable { f.repeat = r.copy(days = if (on) effective - d else effective + d) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(d.getDisplayName(TextStyle.NARROW, formats.locale), color = if (on) c.onAccent else c.textDim, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.labelLarge)
                    }
                }
            }
        }
    }
    FormRow(null) {
        LabeledLine("Ends") {
            val ends = when { r.count != null -> "After ${r.count} times"; r.until != null -> "On ${formats.shortDay(r.until)}"; else -> "Never" }
            ChoiceChip(ends, listOf("never" to "Never", "count" to "After a number of times", "until" to "On a date"), {
                when (it) {
                    "never" -> f.repeat = r.copy(count = null, until = null)
                    "count" -> f.repeat = r.copy(count = r.count ?: 10, until = null)
                    else -> onPickUntil()
                }
            })
        }
    }
    if (r.count != null) {
        FormRow(null) { LabeledLine("Times") { Stepper(r.count, 1..999) { f.repeat = r.copy(count = it) } } }
    }
}

/** − n + for small counts: no keyboard needed. */
@Composable
private fun Stepper(value: Int, range: IntRange, onChange: (Int) -> Unit) {
    val c = Brand.colors
    Row(
        Modifier.clip(RoundedCornerShape(10.dp)).background(c.surface2).border(1.dp, c.hairline, RoundedCornerShape(10.dp)),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("−", color = c.textDim, style = MaterialTheme.typography.titleMedium, modifier = Modifier.clickable(enabled = value > range.first) { onChange(value - 1) }.padding(horizontal = 12.dp, vertical = 6.dp))
        Text(value.toString(), style = Brand.data.copy(fontSize = 14.sp), fontWeight = FontWeight.SemiBold, color = c.text, modifier = Modifier.padding(horizontal = 4.dp))
        Text("+", color = c.textDim, style = MaterialTheme.typography.titleMedium, modifier = Modifier.clickable(enabled = value < range.last) { onChange(value + 1) }.padding(horizontal = 12.dp, vertical = 6.dp))
    }
}

@Composable
private fun RemindersField(f: EventForm) {
    val c = Brand.colors
    var adding by remember { mutableStateOf(false) }
    f.reminders.forEachIndexed { i, r ->
        FormRow(if (i == 0) Glyphs.bell else null) {
            Text(reminderText(r.minutesBefore, f.allDay), modifier = Modifier.weight(1f), color = c.text)
            ChoiceChip(
                if (r.channel == "Email") "Email" else "Notification",
                listOf("WebPush" to "Notification", "Email" to "Email"),
                { ch -> f.reminders = f.reminders.toMutableList().also { it[i] = r.copy(channel = ch) } },
            )
            Box(
                Modifier.padding(start = 4.dp).size(36.dp).clip(CircleShape).clickable { f.reminders = f.reminders.filterIndexed { j, _ -> j != i } },
                contentAlignment = Alignment.Center,
            ) { Icon(Glyphs.close, "Remove reminder", tint = c.textFaint, modifier = Modifier.size(18.dp)) }
        }
    }
    FormRow(if (f.reminders.isEmpty()) Glyphs.bell else null) {
        Box {
            AddLink("Add reminder") { adding = true }
            DropdownMenu(expanded = adding, onDismissRequest = { adding = false }, containerColor = c.surface2, shape = RoundedCornerShape(14.dp)) {
                REMINDER_CHOICES.forEach { m ->
                    DropdownMenuItem(text = { Text(reminderText(m, f.allDay), color = c.text) }, onClick = {
                        adding = false
                        f.reminders = (f.reminders + Reminder(m, "WebPush")).distinct().sortedBy { it.minutesBefore }
                    })
                }
            }
        }
    }
}
