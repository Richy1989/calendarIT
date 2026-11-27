package org.hyperstarit.twentyfourseven.ui.calendar

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import java.time.LocalDate
import java.time.temporal.WeekFields
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.ui.editor.EditTarget
import org.hyperstarit.twentyfourseven.ui.editor.EventEditor
import org.hyperstarit.twentyfourseven.ui.editor.Scope
import org.hyperstarit.twentyfourseven.ui.theme.Banner
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.Segmented
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import org.hyperstarit.twentyfourseven.ui.theme.TopBar

/** The calendar: month, week, day or agenda; the drawer picks which calendars show. */
@Composable
fun CalendarScreen(
    bottomBar: @Composable () -> Unit,
    attention: Boolean,
    onAccounts: () -> Unit,
    onTasks: () -> Unit,
    openDate: kotlinx.coroutines.flow.MutableStateFlow<LocalDate?> = kotlinx.coroutines.flow.MutableStateFlow(null),
    viewModel: CalendarViewModel = hiltViewModel(),
) {
    val requested by openDate.collectAsStateWithLifecycle()
    LaunchedEffect(requested) {
        requested?.let { viewModel.openDay(it); openDate.value = null }
    }
    val c = Brand.colors
    val formats = rememberFormats()
    val calendarFocus by viewModel.focus.collectAsStateWithLifecycle()
    val focus = calendarFocus.date
    val shown by viewModel.shown.collectAsStateWithLifecycle()
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val calendars by viewModel.calendars.collectAsStateWithLifecycle()
    val hidden by viewModel.hidden.collectAsStateWithLifecycle()
    var open by remember { mutableStateOf<Occurrence?>(null) }
    var editing by remember { mutableStateOf<EditTarget?>(null) }
    /** A series' occurrence waiting for "which ones?": true = to edit, false = to delete. */
    var askScope by remember { mutableStateOf<Pair<Occurrence, Boolean>?>(null) }
    val pending by viewModel.pendingDelete.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val canCreate = calendars.any { !it.readOnly && it.allowsEvents }
    val askPermission = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestPermission(),
    ) { }
    val context = androidx.compose.ui.platform.LocalContext.current
    LaunchedEffect(Unit) {
        val allowed = androidx.core.app.NotificationManagerCompat.from(context).areNotificationsEnabled()
        if (!allowed && viewModel.shouldAskForNotifications()) {
            askPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val today = LocalDate.now(formats.zone)

    val isRtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val sidebarButton: @Composable () -> Unit = {
        RoundIconButton(Glyphs.menu, "Open calendar sidebar", { scope.launch { drawer.open() } })
    }
    CalendarDrawer(
        state = drawer,
        sidebar = {
            CalendarSidebar(
                viewModel, focus, formats, accounts, calendars, hidden,
                onClose = { scope.launch { drawer.close() } },
                onDate = { scope.launch {
                    drawer.close()
                    if (viewModel.mode == ViewMode.AGENDA) viewModel.openDay(it) else viewModel.goTo(it)
                } },
                onNew = { scope.launch { drawer.close(); editing = EditTarget.New(focus, nextHour(focus, formats)) } },
                onTasks = { scope.launch { drawer.close(); onTasks() } },
                onAccounts = { scope.launch { drawer.close(); onAccounts() } },
            )
        },
    ) {
        Scaffold(
            containerColor = Color.Transparent,
            snackbarHost = { SnackbarHost(snackbar) },
            bottomBar = bottomBar,
            floatingActionButton = {
                if (canCreate) {
                    Box(
                        Modifier.size(58.dp)
                            .shadow(16.dp, RoundedCornerShape(18.dp), ambientColor = c.highlight, spotColor = c.highlight.copy(alpha = 0.7f * c.glow))
                            .clip(RoundedCornerShape(18.dp)).background(c.grad)
                            .clickable(role = Role.Button) { editing = EditTarget.New(focus, nextHour(focus, formats)) },
                        contentAlignment = Alignment.Center,
                    ) { Icon(Glyphs.plus, "New appointment", tint = c.onAccent, modifier = Modifier.size(26.dp)) }
                }
            },
            topBar = {
                Column {
                    TopBar(
                        title = if (viewModel.mode == ViewMode.DAY) formats.shortDay(focus) else formats.monthTitle(focus),
                        subtitle = when (viewModel.mode) {
                            ViewMode.WEEK -> "Week ${focus.get(WeekFields.of(formats.locale).weekOfWeekBasedYear())}"
                            ViewMode.DAY -> when (focus) {
                                today -> "Today"
                                today.plusDays(1) -> "Tomorrow"
                                today.minusDays(1) -> "Yesterday"
                                else -> focus.year.toString()
                            }
                            ViewMode.AGENDA -> "Coming up"
                            ViewMode.MONTH -> null
                        },
                        navigation = if (isRtl) null else sidebarButton,
                        actions = {
                            TodayButton(today.dayOfMonth, onClick = viewModel::today)
                            RoundIconButton(Glyphs.sync, "Sync now", viewModel::syncNow)
                            if (isRtl) sidebarButton()
                        },
                    )
                    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp).padding(bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        RoundIconButton(Glyphs.chevronLeft, "Previous", { viewModel.step(false) }, filled = false)
                        Segmented(
                            listOf(ViewMode.MONTH to "Month", ViewMode.WEEK to "Week", ViewMode.DAY to "Day", ViewMode.AGENDA to "Agenda"),
                            viewModel.mode, viewModel::show, Modifier.weight(1f).padding(horizontal = 4.dp),
                        )
                        RoundIconButton(Glyphs.chevronRight, "Next", { viewModel.step(true) }, filled = false)
                    }
                    if (viewModel.syncing) {
                        LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp), color = c.highlight, trackColor = Color.Transparent)
                    } else {
                        Hairline()
                    }
                    AnimatedVisibility(attention) {
                        Banner(
                            "An account isn't syncing.", action = "Fix", onClick = onAccounts,
                            modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                        )
                    }
                }
            },
        ) { padding ->
            Box(Modifier.fillMaxSize().padding(padding)) {
                when (viewModel.mode) {
                    ViewMode.MONTH -> MonthView(calendarFocus, shown, formats, viewModel::pageSettled, viewModel::openDay)
                    ViewMode.WEEK -> TimelineView(
                        7, calendarFocus, shown, formats, onFocus = viewModel::pageSettled, onDay = viewModel::openDay, onOpen = { open = it },
                        onCreateAt = if (canCreate) ({ date, time -> editing = EditTarget.New(date, time) }) else null,
                    )
                    ViewMode.DAY -> TimelineView(
                        1, calendarFocus, shown, formats, onFocus = viewModel::pageSettled, onDay = viewModel::openDay, onOpen = { open = it },
                        onCreateAt = if (canCreate) ({ date, time -> editing = EditTarget.New(date, time) }) else null,
                    )
                    ViewMode.AGENDA -> AgendaView(focus, shown, formats, onOpen = { open = it })
                }
            }
        }
    }
    open?.let { o ->
        EventDetailSheet(
            o, formats, onDismiss = { open = null },
            onEdit = { open = null; if (o.seriesId != null) askScope = o to true else editing = EditTarget.Existing(o, null) },
            onDelete = { open = null; if (o.seriesId != null) askScope = o to false else viewModel.delete(o, null) },
            onRespond = { viewModel.respond(o, it); open = null },
        )
    }
    askScope?.let { (o, edit) ->
        ScopeDialog(edit, onDismiss = { askScope = null }) { scope ->
            askScope = null
            if (edit) editing = EditTarget.Existing(o, scope) else viewModel.delete(o, scope)
        }
    }
    editing?.let { EventEditor(it, formats, onClose = { editing = null }) }
    // The undo offer for a delete; when it runs out, the delete goes through.
    LaunchedEffect(pending) {
        val p = pending ?: return@LaunchedEffect
        val result = snackbar.showSnackbar("“${p.occurrence.title}” deleted", actionLabel = "Undo", duration = SnackbarDuration.Long)
        if (result == SnackbarResult.ActionPerformed) viewModel.undoDelete() else viewModel.commitPendingDelete()
    }
}

/** "Today", with today's date on a little calendar leaf, like the web's Today button. */
@Composable
private fun TodayButton(day: Int, onClick: () -> Unit) {
    val c = Brand.colors
    Row(
        Modifier.height(40.dp).clip(RoundedCornerShape(20.dp)).background(c.surface2).border(1.dp, c.hairline, RoundedCornerShape(20.dp))
            .clickable(role = Role.Button, onClick = onClick).padding(start = 6.dp, end = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier.size(28.dp).clip(RoundedCornerShape(14.dp)).background(c.gradSoft).border(1.dp, c.violet.copy(alpha = 0.4f), RoundedCornerShape(14.dp)),
            contentAlignment = Alignment.Center,
        ) { Text(day.toString(), style = Brand.data, fontWeight = FontWeight.SemiBold, color = c.text, fontSize = 12.sp) }
        Spacer(Modifier.width(6.dp))
        Text("Today", style = MaterialTheme.typography.labelLarge, color = c.text)
    }
}

/** Which occurrences of a series a change or delete is for. */
@Composable
private fun ScopeDialog(edit: Boolean, onDismiss: () -> Unit, onPick: (Scope) -> Unit) {
    val c = Brand.colors
    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Glyphs.repeat, null, tint = c.link) },
        title = { Text(if (edit) "Edit a repeating appointment" else "Delete a repeating appointment", style = MaterialTheme.typography.titleLarge) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Which ones should change?", color = c.textDim, style = MaterialTheme.typography.bodyMedium)
                listOf(
                    Triple(Scope.ONE, "Only this one", "The others stay as they are."),
                    Triple(Scope.FOLLOWING, "This and the following ones", "Earlier ones stay as they are."),
                    Triple(Scope.ALL, "All of them", "The whole series."),
                ).forEach { (scope, label, hint) ->
                    Column(
                        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(c.surface)
                            .border(1.dp, if (!edit && scope == Scope.ALL) c.danger.copy(alpha = 0.35f) else c.hairline, RoundedCornerShape(12.dp))
                            .clickable { onPick(scope) }.padding(horizontal = 14.dp, vertical = 10.dp),
                    ) {
                        Text(label, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, color = if (!edit && scope == Scope.ALL) c.dangerInk else c.text)
                        Text(hint, style = MaterialTheme.typography.bodySmall, color = c.textDim)
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextAction("Cancel", onDismiss) },
    )
}

/** A new appointment on [date] starts at the next full hour today, else at 09:00. */
private fun nextHour(date: LocalDate, formats: Formats): java.time.LocalTime {
    val now = java.time.LocalDateTime.now(formats.zone)
    return if (date == now.toLocalDate() && now.hour < 23) java.time.LocalTime.of(now.hour + 1, 0) else java.time.LocalTime.of(9, 0)
}
