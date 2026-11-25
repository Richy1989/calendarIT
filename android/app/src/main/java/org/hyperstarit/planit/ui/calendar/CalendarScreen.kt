package org.hyperstarit.planit.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.core.net.toUri
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.DateRange
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.launch
import org.hyperstarit.planit.calendar.Occurrence
import org.hyperstarit.planit.ui.editor.EditTarget
import org.hyperstarit.planit.ui.editor.EventEditor
import org.hyperstarit.planit.ui.editor.Scope

/** The calendar: month, week, day or agenda; the drawer picks calendars and leads to the accounts. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CalendarScreen(onAccounts: () -> Unit, onTasks: () -> Unit, viewModel: CalendarViewModel = hiltViewModel()) {
    val formats = rememberFormats()
    val focus by viewModel.focus.collectAsStateWithLifecycle()
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
    val canCreate = calendars.any { !it.readOnly }
    val context = androidx.compose.ui.platform.LocalContext.current
    var notificationsAllowed by remember { mutableStateOf(androidx.core.app.NotificationManagerCompat.from(context).areNotificationsEnabled()) }
    val askPermission = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestPermission(),
    ) { notificationsAllowed = it }
    val askCalendar = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions(),
    ) { granted -> if (granted.values.all { it }) viewModel.showInSystemCalendar(true) }
    LaunchedEffect(Unit) {
        if (!notificationsAllowed && viewModel.shouldAskForNotifications()) {
            askPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    var viewMenu by remember { mutableStateOf(false) }
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val problem = accounts.any { it.needsSignIn || it.certificateChanged }

    ModalNavigationDrawer(
        drawerState = drawer,
        drawerContent = {
            ModalDrawerSheet {
                Column(Modifier.verticalScroll(rememberScrollState()).padding(vertical = 12.dp)) {
                    Text("PlanIt", style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(horizontal = 24.dp, vertical = 12.dp))
                    accounts.forEach { account ->
                        Text(
                            account.username, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(start = 24.dp, top = 12.dp, bottom = 4.dp),
                        )
                        calendars.filter { it.accountId == account.id }.forEach { calendar ->
                            ListItem(
                                leadingContent = {
                                    Box(Modifier.size(12.dp).clip(CircleShape).background(parseColor(calendar.color) ?: MaterialTheme.colorScheme.primary))
                                },
                                trailingContent = {
                                    Checkbox(checked = calendar.id !in hidden, onCheckedChange = { viewModel.setHidden(calendar.id, !it) })
                                },
                                modifier = Modifier.padding(horizontal = 8.dp),
                            ) { Text(calendar.name + if (calendar.readOnly) " · read-only" else "") }
                        }
                    }
                    HorizontalDivider(Modifier.padding(vertical = 8.dp))
                    ListItem(
                        supportingContent = {
                            if (viewModel.remindersOn && !notificationsAllowed) {
                                Text("Notifications are off for PlanIt.", color = MaterialTheme.colorScheme.error)
                            } else {
                                Text("Notification reminders ring here; email ones come from the server.")
                            }
                        },
                        trailingContent = { androidx.compose.material3.Switch(viewModel.remindersOn, viewModel::turnReminders) },
                        modifier = Modifier.padding(horizontal = 8.dp),
                    ) { Text("Reminders on this phone") }
                    ListItem(
                        supportingContent = { Text("Other calendar apps show your appointments and can change them.") },
                        trailingContent = {
                            androidx.compose.material3.Switch(viewModel.inSystemCalendar, { on ->
                                if (on) {
                                    askCalendar.launch(arrayOf(android.Manifest.permission.READ_CALENDAR, android.Manifest.permission.WRITE_CALENDAR))
                                } else {
                                    viewModel.showInSystemCalendar(false)
                                }
                            })
                        },
                        modifier = Modifier.padding(horizontal = 8.dp),
                    ) { Text("Show in phone's calendar") }
                    ListItem(
                        supportingContent = {
                            Text(
                                when {
                                    viewModel.pushDistributor != null -> "On, through ${appLabel(context, viewModel.pushDistributor!!)}."
                                    viewModel.pushInstalled -> "Tap to use your UnifiedPush app."
                                    else -> "Changes from elsewhere arrive within 15 minutes. Install a UnifiedPush app such as ntfy to get them at once."
                                },
                            )
                        },
                        modifier = Modifier.padding(horizontal = 8.dp).clickable {
                            if (viewModel.pushInstalled) {
                                (context as? android.app.Activity)?.let(viewModel::setUpInstantUpdates)
                            } else {
                                context.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, "https://unifiedpush.org/users/distributors/".toUri()))
                            }
                        },
                    ) { Text("Instant updates") }
                    if (viewModel.remindersOn && !notificationsAllowed) {
                        TextButton(onClick = {
                            context.startActivity(
                                android.content.Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                                    .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, context.packageName),
                            )
                        }, modifier = Modifier.padding(horizontal = 16.dp)) { Text("Turn on notifications") }
                    }
                    NavigationDrawerItem(
                        icon = { Icon(Icons.Default.AccountCircle, contentDescription = null) },
                        label = { Text(if (problem) "Accounts · needs attention" else "Accounts") },
                        selected = false,
                        onClick = { scope.launch { drawer.close() }; onAccounts() },
                        modifier = Modifier.padding(horizontal = 12.dp),
                    )
                }
            }
        },
    ) {
        Scaffold(
            snackbarHost = { SnackbarHost(snackbar) },
            floatingActionButton = {
                if (canCreate) {
                    FloatingActionButton(onClick = { editing = EditTarget.New(focus, nextHour(focus, formats)) }) {
                        Icon(Icons.Default.Add, contentDescription = "New appointment")
                    }
                }
            },
            topBar = {
                TopAppBar(
                    navigationIcon = {
                        IconButton(onClick = { scope.launch { drawer.open() } }) { Icon(Icons.Default.Menu, contentDescription = "Calendars and accounts") }
                    },
                    title = {
                        TextButton(onClick = { viewMenu = true }) {
                            Text(
                                if (viewModel.mode == ViewMode.DAY) formats.shortDay(focus) else formats.monthTitle(focus),
                                style = MaterialTheme.typography.titleLarge,
                            )
                            Icon(Icons.Default.ArrowDropDown, contentDescription = "Change view")
                        }
                        DropdownMenu(expanded = viewMenu, onDismissRequest = { viewMenu = false }) {
                            listOf(ViewMode.MONTH to "Month", ViewMode.WEEK to "Week", ViewMode.DAY to "Day", ViewMode.AGENDA to "Agenda").forEach { (mode, label) ->
                                DropdownMenuItem(text = { Text(label) }, onClick = { viewMenu = false; viewModel.show(mode) })
                            }
                        }
                    },
                    actions = {
                        IconButton(onClick = onTasks) { Icon(Icons.AutoMirrored.Filled.List, contentDescription = "Tasks") }
                        IconButton(onClick = viewModel::today) { Icon(Icons.Default.DateRange, contentDescription = "Today") }
                        IconButton(onClick = viewModel::syncNow) { Icon(Icons.Default.Refresh, contentDescription = "Sync now") }
                    },
                )
            },
        ) { padding ->
            Column(Modifier.fillMaxSize().padding(padding)) {
                if (viewModel.syncing) LinearProgressIndicator(Modifier.fillMaxWidth())
                if (problem) {
                    TextButton(onClick = onAccounts, modifier = Modifier.padding(horizontal = 8.dp)) {
                        Text("An account needs attention — it isn't syncing.", color = MaterialTheme.colorScheme.error)
                    }
                }
                Box(Modifier.fillMaxSize()) {
                    when (viewModel.mode) {
                        ViewMode.MONTH -> MonthView(focus, shown, formats, onMonthShown = { viewModel.focus.value = it }, onDay = viewModel::openDay)
                        ViewMode.WEEK -> TimelineView(
                            7, focus, shown, formats, onFocus = { viewModel.focus.value = it }, onDay = viewModel::openDay, onOpen = { open = it },
                            onCreateAt = if (canCreate) ({ date, time -> editing = EditTarget.New(date, time) }) else null,
                        )
                        ViewMode.DAY -> TimelineView(
                            1, focus, shown, formats, onFocus = { viewModel.focus.value = it }, onDay = viewModel::openDay, onOpen = { open = it },
                            onCreateAt = if (canCreate) ({ date, time -> editing = EditTarget.New(date, time) }) else null,
                        )
                        ViewMode.AGENDA -> AgendaView(focus, shown, formats, onOpen = { open = it })
                    }
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

/** Which occurrences of a series a change or delete is for. */
@Composable
private fun ScopeDialog(edit: Boolean, onDismiss: () -> Unit, onPick: (Scope) -> Unit) {
    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (edit) "Edit a repeating appointment" else "Delete a repeating appointment") },
        text = {
            Column {
                listOf(Scope.ONE to "Only this one", Scope.FOLLOWING to "This and the following ones", Scope.ALL to "All of them").forEach { (scope, label) ->
                    TextButton(onClick = { onPick(scope) }, modifier = Modifier.fillMaxWidth()) { Text(label, modifier = Modifier.fillMaxWidth()) }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** An installed app's name, for "through ntfy". */
private fun appLabel(context: android.content.Context, packageName: String): String = runCatching {
    val pm = context.packageManager
    pm.getApplicationLabel(pm.getApplicationInfo(packageName, 0)).toString()
}.getOrDefault(packageName)

/** A new appointment on [date] starts at the next full hour today, else at 09:00. */
private fun nextHour(date: java.time.LocalDate, formats: Formats): java.time.LocalTime {
    val now = java.time.LocalDateTime.now(formats.zone)
    return if (date == now.toLocalDate() && now.hour < 23) java.time.LocalTime.of(now.hour + 1, 0) else java.time.LocalTime.of(9, 0)
}
