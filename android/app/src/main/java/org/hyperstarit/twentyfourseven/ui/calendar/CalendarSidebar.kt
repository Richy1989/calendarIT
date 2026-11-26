package org.hyperstarit.twentyfourseven.ui.calendar

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import java.time.LocalDate
import java.time.temporal.TemporalAdjusters
import kotlinx.coroutines.flow.flowOf
import org.hyperstarit.twentyfourseven.calendar.DateRange
import org.hyperstarit.twentyfourseven.calendar.Occurrences
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.scopedKey
import org.hyperstarit.twentyfourseven.ui.theme.*

@Composable
internal fun CalendarDrawer(state: DrawerState, sidebar: @Composable () -> Unit, content: @Composable () -> Unit) {
    val direction = LocalLayoutDirection.current
    BoxWithConstraints {
        val width = minOf(360.dp, maxWidth * .83f)
        // Anchor the drawer to the physical left edge in every locale.
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
            ModalNavigationDrawer(
                drawerState = state,
                gesturesEnabled = state.isOpen,
                scrimColor = Brand.colors.overlay.copy(alpha = .55f),
                drawerContent = {
                    ModalDrawerSheet(
                        drawerState = state, modifier = Modifier.width(width),
                        drawerContainerColor = Brand.colors.surface,
                        drawerShape = RoundedCornerShape(topEnd = 20.dp, bottomEnd = 20.dp),
                    ) {
                        CompositionLocalProvider(LocalLayoutDirection provides direction) {
                            if (state.isOpen || state.targetValue == DrawerValue.Open || state.isAnimationRunning) sidebar()
                        }
                    }
                },
            ) { CompositionLocalProvider(LocalLayoutDirection provides direction, content = content) }
        }
    }
}

@Composable
internal fun CalendarSidebar(
    model: CalendarViewModel, focus: LocalDate, formats: Formats, accounts: List<AccountEntity>,
    calendars: List<CalendarEntity>, hidden: Set<String>, onClose: () -> Unit, onDate: (LocalDate) -> Unit,
    onNew: () -> Unit, onTasks: () -> Unit, onAccounts: () -> Unit,
) {
    val c = Brand.colors
    val categories by model.categories.collectAsStateWithLifecycle()
    val hiddenCategories by model.hiddenCategories.collectAsStateWithLifecycle()
    val uri = LocalUriHandler.current
    var manage by rememberSaveable { mutableStateOf<String?>(null) }
    var launchError by rememberSaveable { mutableStateOf(false) }
    fun openSettings(account: AccountEntity, section: String) {
        manage = null
        try { uri.openUri("${account.server}?settings=$section") }
        catch (_: IllegalArgumentException) { launchError = true }
    }
    fun manage(section: String) {
        accounts.singleOrNull()?.let { openSettings(it, section) } ?: run { manage = section }
    }
    Column(Modifier.fillMaxSize()) {
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp)) {
            Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                BrandMark(28.dp)
                Text("24seven", Modifier.weight(1f).padding(start = 10.dp), style = MaterialTheme.typography.titleLarge, color = c.text)
                RoundIconButton(Glyphs.close, "Close sidebar", onClose, filled = false)
            }
            PrimaryButton("New appointment", onNew, Modifier.fillMaxWidth(), enabled = calendars.any { !it.readOnly }, icon = Glyphs.plus)
            TextAction("Tasks", onTasks)
            SidebarMonth(model, focus, formats, onDate)
            listOf(false, true).forEach { subscription ->
                val rows = calendars.filter { it.readOnly == subscription }
                if (!subscription || rows.isNotEmpty()) {
                    SidebarGroup(if (subscription) "Subscriptions" else "My calendars") {
                        rows.forEach { cal ->
                            val key = scopedKey(cal.accountId, cal.id)
                            FilterRow(cal.name, accountLabel(accounts, cal.accountId), parseColor(cal.color) ?: c.accent,
                                key !in hidden && cal.id !in hidden, { model.filterCalendar(key, it) })
                            cal.subscriptionError?.let { message ->
                                Text(message, Modifier.padding(start = 30.dp, bottom = 6.dp), color = c.dangerInk, style = MaterialTheme.typography.bodySmall)
                            }
                        }
                    }
                }
            }
            TextAction("Manage or subscribe…", { manage("calendars") })
            SidebarGroup("Categories") {
                categories.forEach { cat ->
                    val key = scopedKey(cat.accountId, cat.id)
                    FilterRow(cat.name, accountLabel(accounts, cat.accountId), parseColor(cat.color) ?: c.accent,
                        key !in hiddenCategories, { model.filterCategory(key, it) })
                }
                FilterRow("Uncategorized", null, c.textFaint, Occurrences.UNCATEGORIZED !in hiddenCategories,
                    { model.filterCategory(Occurrences.UNCATEGORIZED, it) })
                TextAction("Manage categories…", { manage("categories") })
            }
        }
        Hairline()
        Row(Modifier.fillMaxWidth().clickable(onClick = onAccounts).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(accounts.singleOrNull()?.username ?: "Manage accounts", color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val issue = accounts.firstOrNull { it.lastError != null || it.needsSignIn || it.certificateChanged }
                Text(issue?.lastError ?: if (issue != null) "Sync needs attention" else accounts.singleOrNull()?.server ?: "${accounts.size} accounts",
                    color = if (issue != null) c.dangerInk else c.textDim, style = MaterialTheme.typography.bodySmall,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            Icon(Glyphs.settings, "Manage accounts", tint = c.textDim)
        }
    }
    manage?.let { section ->
        AlertDialog(onDismissRequest = { manage = null }, title = { Text("Choose an account") },
            text = { Column(Modifier.verticalScroll(rememberScrollState())) { accounts.forEach {
                TextAction("${it.username} · ${it.server}", { openSettings(it, section) })
            } } }, confirmButton = { TextAction("Cancel", { manage = null }) })
    }
    if (launchError) AlertDialog(onDismissRequest = { launchError = false }, text = { Text("No browser could open the server settings.") },
        confirmButton = { TextAction("Close", { launchError = false }) })
}

private fun accountLabel(accounts: List<AccountEntity>, id: String) =
    if (accounts.size > 1) accounts.find { it.id == id }?.username else null

@Composable
private fun SidebarGroup(title: String, content: @Composable () -> Unit) {
    var expanded by rememberSaveable { mutableStateOf(true) }
    Row(Modifier.fillMaxWidth().clickable { expanded = !expanded }.heightIn(min = 48.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Icon(if (expanded) Glyphs.chevronDown else Glyphs.chevronRight, if (expanded) "Collapse" else "Expand",
            Modifier.size(18.dp), tint = Brand.colors.textDim)
        Text(title, Modifier.padding(start = 6.dp), style = MaterialTheme.typography.titleSmall, color = Brand.colors.text)
    }
    if (expanded) content()
}

@Composable
private fun FilterRow(name: String, account: String?, color: Color, visible: Boolean, change: (only: Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Row(Modifier.weight(1f).toggleable(visible, role = Role.Checkbox) { change(false) }.heightIn(min = 44.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Checkbox(visible, onCheckedChange = null, colors = CheckboxDefaults.colors(checkedColor = color, checkmarkColor = onColor(color)))
            Column(Modifier.padding(start = 10.dp)) {
                Text(name, color = Brand.colors.text, style = MaterialTheme.typography.bodyMedium)
                if (account != null) Text(account, color = Brand.colors.textDim, style = MaterialTheme.typography.bodySmall)
            }
        }
        TextAction("Only", { change(true) }, Modifier.semantics { contentDescription = "Show only $name" })
    }
}

@Composable
private fun SidebarMonth(model: CalendarViewModel, focus: LocalDate, formats: Formats, onDate: (LocalDate) -> Unit) {
    val c = Brand.colors
    var offset by rememberSaveable(focus.withDayOfMonth(1)) { mutableIntStateOf(0) }
    val month = focus.withDayOfMonth(1).plusMonths(offset.toLong())
    val first = month.with(TemporalAdjusters.previousOrSame(formats.firstDayOfWeek))
    val appointments = remember(model, first) { model.appointments(flowOf(DateRange(first, first.plusWeeks(6)))) }
    val shown by appointments.collectAsStateWithLifecycle(initialValue = null)
    Column(Modifier.fillMaxWidth().pointerInput(month) {
        val threshold = 32.dp.toPx()
        var distance = 0f
        detectHorizontalDragGestures(
            onDragStart = { distance = 0f },
            onDragEnd = {
                when {
                    distance <= -threshold -> offset++
                    distance >= threshold -> offset--
                }
            },
        ) { change, delta ->
            change.consume()
            distance += delta
        }
    }) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(formats.monthTitle(month), Modifier.weight(1f), color = c.text, style = MaterialTheme.typography.titleMedium)
            RoundIconButton(Glyphs.chevronLeft, "Previous month", { offset-- }, filled = false)
            RoundIconButton(Glyphs.chevronRight, "Next month", { offset++ }, filled = false)
        }
        Row { repeat(7) {
            Box(Modifier.weight(1f).height(26.dp), contentAlignment = Alignment.Center) {
                Text(formats.dayName(first.plusDays(it.toLong()).dayOfWeek).take(1), style = Brand.label, color = c.textDim)
            }
        } }
        repeat(6) { week ->
            val start = first.plusWeeks(week.toLong())
            val active = focus >= start && focus < start.plusWeeks(1)
            Row(Modifier.fillMaxWidth().background(if (active) c.surface2 else Color.Transparent, CircleShape)) {
                repeat(7) { index ->
                    val date = start.plusDays(index.toLong())
                    val selected = date == focus
                    val dots = shown?.on(date).orEmpty().map { it.color }.distinct().take(3)
                    Box(Modifier.weight(1f).height(40.dp).clickable { onDate(date) }
                        .semantics { contentDescription = formats.longDate(date) }, contentAlignment = Alignment.Center) {
                        Box(Modifier.size(28.dp).background(if (selected) c.accent else Color.Transparent, CircleShape),
                            contentAlignment = Alignment.Center) {
                            Text(date.dayOfMonth.toString(), style = MaterialTheme.typography.labelLarge,
                                color = if (selected) c.onAccent else if (date.month == month.month) c.text else c.textFaint)
                        }
                        Row(Modifier.align(Alignment.BottomCenter).padding(bottom = 2.dp), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
                            dots.forEach { Box(Modifier.size(3.dp).clip(CircleShape).background(parseColor(it) ?: c.accent)) }
                        }
                    }
                }
            }
        }
    }
}
