package org.hyperstarit.twentyfourseven.ui.settings

import android.content.Intent
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.BuildConfig
import org.hyperstarit.twentyfourseven.push.PushRegistrar
import org.hyperstarit.twentyfourseven.reminders.ReminderPrefs
import org.hyperstarit.twentyfourseven.reminders.ReminderScheduler
import org.hyperstarit.twentyfourseven.system.SystemCalendar
import org.hyperstarit.twentyfourseven.ui.theme.AppearancePrefs
import org.hyperstarit.twentyfourseven.ui.theme.BrandMark
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.Palette
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.BrandSwitch
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.SettingRow
import org.hyperstarit.twentyfourseven.ui.theme.ThemeChoice
import org.hyperstarit.twentyfourseven.ui.theme.TopBar

@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val appScope: CoroutineScope,
    private val appearance: AppearancePrefs,
    private val reminderPrefs: ReminderPrefs,
    private val reminders: ReminderScheduler,
    private val system: SystemCalendar,
    private val push: PushRegistrar,
) : ViewModel() {
    val theme = appearance.theme

    fun setTheme(choice: ThemeChoice) = appearance.setTheme(choice)

    /** The UnifiedPush distributor in use (package name), or null: then the app syncs on a schedule. */
    var pushDistributor by mutableStateOf(push.current())
        private set
    val pushInstalled: Boolean get() = push.distributors().isNotEmpty()

    /** Picks (or confirms) the distributor — asking the user when there are several — and registers. */
    fun setUpInstantUpdates(activity: android.app.Activity) {
        org.unifiedpush.android.connector.UnifiedPush.tryPickDistributor(activity) { ok ->
            pushDistributor = push.current()
            if (ok) appScope.launch { push.registerAll() }
        }
    }

    var remindersOn by mutableStateOf(reminderPrefs.enabled)
        private set
    var inSystemCalendar by mutableStateOf(system.enabled && system.permitted())
        private set

    /** On: after the calendar permission was granted. Off: the system calendar loses 24seven's calendars. */
    fun showInSystemCalendar(on: Boolean) {
        inSystemCalendar = on
        appScope.launch {
            if (on) {
                system.enabled = true
                system.sync()
            } else {
                system.turnOff()
            }
        }
    }

    fun turnReminders(on: Boolean) {
        reminderPrefs.enabled = on
        remindersOn = on
        appScope.launch { reminders.reschedule() }
    }
}

/** Everything about this device's 24seven: look, reminders, the phone's calendar, push, accounts. */
@Composable
fun SettingsScreen(
    bottomBar: @Composable () -> Unit,
    attention: Boolean,
    onAccounts: () -> Unit,
    viewModel: SettingsViewModel = hiltViewModel(),
) {
    val context = LocalContext.current
    val c = Brand.colors
    val theme by viewModel.theme.collectAsStateWithLifecycle()
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var notificationsAllowed by remember { mutableStateOf(androidx.core.app.NotificationManagerCompat.from(context).areNotificationsEnabled()) }
    // Coming back from the system's notification settings.
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) {
        notificationsAllowed = androidx.core.app.NotificationManagerCompat.from(context).areNotificationsEnabled()
    }
    val askCalendar = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { granted ->
        if (granted.values.all { it }) viewModel.showInSystemCalendar(true)
    }

    Scaffold(containerColor = Color.Transparent, bottomBar = bottomBar, topBar = { TopBar("Settings") }) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            SectionLabel("Accounts", Modifier.padding(start = 4.dp, top = 8.dp))
            Panel(padding = 0.dp, border = if (attention) c.danger.copy(alpha = 0.4f) else c.hairline) {
                SettingRow(
                    Glyphs.user, "Accounts",
                    subtitle = if (attention) "An account needs your attention — it isn't syncing." else "Servers, calendars and sync status",
                    subtitleColor = if (attention) c.dangerInk else c.textDim,
                    onClick = onAccounts,
                    trailing = { Icon(Glyphs.chevronRight, null, tint = c.textFaint, modifier = Modifier.size(20.dp)) },
                )
            }

            SectionLabel("Appearance", Modifier.padding(start = 4.dp, top = 14.dp))
            Panel {
                Text("Theme", style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, color = c.text)
                Text("The same looks as the web app.", style = MaterialTheme.typography.bodySmall, color = c.textDim)
                Spacer(Modifier.height(12.dp))
                val systemDark = androidx.compose.foundation.isSystemInDarkTheme()
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    ThemeChoice.entries.chunked(3).forEach { row ->
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            row.forEach { choice ->
                                ThemeSwatch(choice, choice.palette(systemDark), selected = theme == choice, Modifier.weight(1f)) { viewModel.setTheme(choice) }
                            }
                            repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
                        }
                    }
                }
            }

            SectionLabel("Reminders", Modifier.padding(start = 4.dp, top = 14.dp))
            Panel(padding = 0.dp) {
                SettingRow(
                    Glyphs.bell, "Reminders on this phone",
                    subtitle = if (viewModel.remindersOn && !notificationsAllowed) "Notifications are off for 24seven — tap to turn them on."
                    else "Notification reminders ring here; email reminders come from the server.",
                    subtitleColor = if (viewModel.remindersOn && !notificationsAllowed) c.dangerInk else c.textDim,
                    onClick = if (viewModel.remindersOn && !notificationsAllowed) {
                        {
                            context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName))
                        }
                    } else null,
                    trailing = { BrandSwitch(viewModel.remindersOn, viewModel::turnReminders) },
                )
            }

            SectionLabel("This phone", Modifier.padding(start = 4.dp, top = 14.dp))
            Panel(padding = 0.dp) {
                SettingRow(
                    Glyphs.phone, "Show in phone's calendar",
                    subtitle = "Other calendar apps show your appointments and can change them.",
                    trailing = {
                        BrandSwitch(viewModel.inSystemCalendar, { on ->
                            if (on) askCalendar.launch(arrayOf(android.Manifest.permission.READ_CALENDAR, android.Manifest.permission.WRITE_CALENDAR))
                            else viewModel.showInSystemCalendar(false)
                        })
                    },
                )
                Hairline(Modifier.padding(start = 66.dp))
                SettingRow(
                    Glyphs.zap, "Instant updates",
                    subtitle = when {
                        viewModel.pushDistributor != null -> "On, through ${appLabel(context, viewModel.pushDistributor!!)}."
                        viewModel.pushInstalled -> "Tap to receive changes at once through your UnifiedPush app."
                        else -> "Changes from elsewhere arrive within 15 minutes. Install a UnifiedPush app such as ntfy to get them at once."
                    },
                    onClick = {
                        if (viewModel.pushInstalled) {
                            (context as? android.app.Activity)?.let(viewModel::setUpInstantUpdates)
                        } else {
                            context.startActivity(Intent(Intent.ACTION_VIEW, "https://unifiedpush.org/users/distributors/".toUri()))
                        }
                    },
                    trailing = if (viewModel.pushDistributor != null) {
                        { StatusPill("On") }
                    } else null,
                )
                Hairline(Modifier.padding(start = 66.dp))
                SettingRow(
                    Glyphs.widget, "Month widget",
                    subtitle = "Put the whole month on your home screen.",
                    onClick = { scope.launch { org.hyperstarit.twentyfourseven.widget.MonthWidgetPin.request(context) } },
                    trailing = { Icon(Glyphs.plus, "Add widget", tint = c.textFaint, modifier = Modifier.size(20.dp)) },
                )
            }

            Column(Modifier.fillMaxWidth().padding(top = 28.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                BrandMark(36.dp)
                Spacer(Modifier.height(8.dp))
                Text("24seven", style = MaterialTheme.typography.titleLarge, color = c.text)
                SectionLabel("Version ${BuildConfig.VERSION_NAME}", Modifier.padding(top = 2.dp))
            }
        }
    }
}

/** A miniature of a theme: its canvas, a surface card, and the spectrum. */
@Composable
private fun ThemeSwatch(choice: ThemeChoice, palette: Palette, selected: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val c = Brand.colors
    Column(
        modifier.clip(RoundedCornerShape(14.dp)).clickable(role = Role.RadioButton, onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(
            Modifier.fillMaxWidth().height(64.dp).clip(RoundedCornerShape(12.dp))
                .background(palette.bg)
                .border(if (selected) 2.dp else 1.dp, if (selected) c.accent else c.hairline, RoundedCornerShape(12.dp))
                .padding(8.dp),
        ) {
            if (choice == ThemeChoice.AUTO) {
                // Half Daylight, half Midnight.
                Box(Modifier.fillMaxSize().clip(RoundedCornerShape(6.dp)).background(Palette.Daylight.bg))
                Box(Modifier.fillMaxSize().padding(start = 24.dp).clip(RoundedCornerShape(6.dp)).background(Palette.Midnight.surface))
            } else {
                Column(Modifier.fillMaxSize().clip(RoundedCornerShape(6.dp)).background(palette.surface).padding(6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Box(Modifier.fillMaxWidth(0.8f).height(5.dp).clip(RoundedCornerShape(3.dp)).background(palette.grad))
                    Box(Modifier.fillMaxWidth(0.55f).height(4.dp).clip(RoundedCornerShape(2.dp)).background(palette.hairlineStrong))
                    Box(Modifier.fillMaxWidth(0.7f).height(4.dp).clip(RoundedCornerShape(2.dp)).background(palette.hairline))
                }
            }
        }
        Text(
            choice.label, style = MaterialTheme.typography.labelMedium, color = if (selected) c.text else c.textDim,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal, modifier = Modifier.padding(top = 6.dp, bottom = 2.dp), maxLines = 1,
        )
    }
}

@Composable
private fun StatusPill(text: String) {
    val c = Brand.colors
    Text(
        text, style = Brand.label, color = c.highlight,
        modifier = Modifier.clip(RoundedCornerShape(99.dp)).background(c.highlight.copy(alpha = 0.12f))
            .border(1.dp, c.highlight.copy(alpha = 0.35f), RoundedCornerShape(99.dp)).padding(horizontal = 10.dp, vertical = 4.dp),
    )
}

/** An installed app's name, for "through ntfy". */
private fun appLabel(context: android.content.Context, packageName: String): String = runCatching {
    val pm = context.packageManager
    pm.getApplicationLabel(pm.getApplicationInfo(packageName, 0)).toString()
}.getOrDefault(packageName)
