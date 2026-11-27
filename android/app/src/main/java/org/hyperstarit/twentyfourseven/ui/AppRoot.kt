package org.hyperstarit.twentyfourseven.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.ui.accounts.AddAccountScreen
import org.hyperstarit.twentyfourseven.ui.calendar.CalendarScreen
import org.hyperstarit.twentyfourseven.ui.home.AccountsScreen
import org.hyperstarit.twentyfourseven.ui.profile.ProfileScreen
import org.hyperstarit.twentyfourseven.ui.settings.SettingsScreen
import org.hyperstarit.twentyfourseven.ui.settings.WidgetSettingsScreen
import org.hyperstarit.twentyfourseven.ui.tasks.TasksScreen
import org.hyperstarit.twentyfourseven.ui.theme.AmbientBackground
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Brand

@HiltViewModel
class RootViewModel @Inject constructor(accounts: Accounts) : ViewModel() {
    /** Null until the accounts are read (the splash stays up meanwhile). */
    val hasAccounts = accounts.all.map { it.isNotEmpty() }.stateIn(viewModelScope, SharingStarted.Eagerly, null)

    /** An account that stopped syncing (signed out, changed certificate): the Settings tab shows a dot. */
    val attention = accounts.all.map { all -> all.any { it.needsSignIn || it.certificateChanged } }
        .stateIn(viewModelScope, SharingStarted.Eagerly, false)
    var screen by mutableStateOf(Screen.CALENDAR)

    /** The account whose profile [Screen.PROFILE] shows. */
    var profileAccount by mutableStateOf<String?>(null)

    /** A day to open (from the widget), taken by the calendar once shown. */
    val openDate = kotlinx.coroutines.flow.MutableStateFlow<java.time.LocalDate?>(null)
}

/** The three tabs of the bottom bar, and the pages Settings leads to. */
enum class Screen(val depth: Int) { CALENDAR(0), TASKS(0), SETTINGS(0), ACCOUNTS(1), ADD_ACCOUNT(2), WIDGETS(1), PROFILE(2) }

/** No account yet → the welcome and sign-in; otherwise the tabs (calendar, tasks, settings) and the account pages. */
@Composable
fun AppRoot(viewModel: RootViewModel) {
    val hasAccounts by viewModel.hasAccounts.collectAsStateWithLifecycle()
    val attention by viewModel.attention.collectAsStateWithLifecycle()
    val go = { s: Screen -> viewModel.screen = s }
    AmbientBackground {
        when (hasAccounts) {
            null -> Unit
            false -> AddAccountScreen(onAdded = {}, onCancel = null)
            true -> {
                val bar: @Composable () -> Unit = { NavBar(viewModel.screen, attention, go) }
                // Back from a side tab returns to the calendar, the app's home.
                BackHandler(enabled = viewModel.screen == Screen.TASKS || viewModel.screen == Screen.SETTINGS) { go(Screen.CALENDAR) }
                AnimatedContent(
                    targetState = viewModel.screen,
                    transitionSpec = {
                        if (targetState.depth == initialState.depth) {
                            fadeIn(tween(180)) togetherWith fadeOut(tween(120))
                        } else {
                            val dir = if (targetState.depth > initialState.depth) 1 else -1
                            (slideInHorizontally(tween(260)) { it / 5 * dir } + fadeIn(tween(220))) togetherWith
                                (slideOutHorizontally(tween(260)) { -it / 5 * dir } + fadeOut(tween(160)))
                        }
                    },
                    label = "screen",
                ) { screen ->
                    when (screen) {
                        Screen.CALENDAR -> CalendarScreen(
                            bottomBar = bar, attention = attention, onAccounts = { go(Screen.ACCOUNTS) }, onTasks = { go(Screen.TASKS) }, openDate = viewModel.openDate,
                        )
                        Screen.TASKS -> TasksScreen(bottomBar = bar)
                        Screen.SETTINGS -> SettingsScreen(
                            bottomBar = bar, attention = attention, onAccounts = { go(Screen.ACCOUNTS) }, onWidgets = { go(Screen.WIDGETS) },
                        )
                        Screen.WIDGETS -> WidgetSettingsScreen(onBack = { go(Screen.SETTINGS) })
                        Screen.ACCOUNTS -> AccountsScreen(
                            onBack = { go(Screen.SETTINGS) }, onAddAccount = { go(Screen.ADD_ACCOUNT) },
                            onProfile = { viewModel.profileAccount = it.id; go(Screen.PROFILE) },
                        )
                        Screen.PROFILE -> viewModel.profileAccount?.let { id -> ProfileScreen(id, onBack = { go(Screen.ACCOUNTS) }) }
                        Screen.ADD_ACCOUNT -> AddAccountScreen(onAdded = { go(Screen.CALENDAR) }, onCancel = { go(Screen.ACCOUNTS) })
                    }
                }
            }
        }
    }
}

/** The bottom bar: three destinations, the current one on the soft gradient. */
@Composable
private fun NavBar(current: Screen, attention: Boolean, onSelect: (Screen) -> Unit) {
    val c = Brand.colors
    Column(Modifier.fillMaxWidth().background(c.surface.copy(alpha = 0.96f))) {
        Box(Modifier.fillMaxWidth().size(1.dp).background(c.hairline))
        Row(
            Modifier.fillMaxWidth().navigationBarsPadding().padding(horizontal = 12.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.SpaceAround,
        ) {
            NavItem(Glyphs.calendar, "Calendar", current == Screen.CALENDAR, dot = false) { onSelect(Screen.CALENDAR) }
            NavItem(Glyphs.tasks, "Tasks", current == Screen.TASKS, dot = false) { onSelect(Screen.TASKS) }
            NavItem(Glyphs.settings, "Settings", current == Screen.SETTINGS, dot = attention) { onSelect(Screen.SETTINGS) }
        }
    }
}

@Composable
private fun NavItem(icon: ImageVector, label: String, selected: Boolean, dot: Boolean, onClick: () -> Unit) {
    val c = Brand.colors
    Column(
        Modifier.clip(RoundedCornerShape(16.dp))
            .clickable(remember { MutableInteractionSource() }, indication = null, role = Role.Tab, onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 2.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box {
            Box(
                Modifier.clip(RoundedCornerShape(14.dp))
                    .then(if (selected) Modifier.background(c.gradSoft).border(1.dp, c.violet.copy(alpha = 0.45f), RoundedCornerShape(14.dp)) else Modifier)
                    .padding(horizontal = 20.dp, vertical = 5.dp),
            ) { Icon(icon, label, tint = if (selected) c.text else c.textFaint, modifier = Modifier.size(22.dp)) }
            if (dot) Box(Modifier.align(Alignment.TopEnd).offset((-12).dp, 3.dp).size(8.dp).clip(CircleShape).background(c.danger))
        }
        Text(
            label, style = MaterialTheme.typography.labelMedium, color = if (selected) c.text else c.textFaint,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium, modifier = Modifier.padding(top = 3.dp),
        )
    }
}
