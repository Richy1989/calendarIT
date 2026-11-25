package org.hyperstarit.planit.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.ui.accounts.AddAccountScreen
import org.hyperstarit.planit.ui.calendar.CalendarScreen
import org.hyperstarit.planit.ui.home.AccountsScreen
import org.hyperstarit.planit.ui.tasks.TasksScreen

@HiltViewModel
class RootViewModel @Inject constructor(accounts: Accounts) : ViewModel() {
    /** Null until the accounts are read (the splash stays up meanwhile). */
    val hasAccounts = accounts.all.map { it.isNotEmpty() }.stateIn(viewModelScope, SharingStarted.Eagerly, null)
    var screen by mutableStateOf(Screen.CALENDAR)

    /** A day to open (from the widget), taken by the calendar once shown. */
    val openDate = kotlinx.coroutines.flow.MutableStateFlow<java.time.LocalDate?>(null)
}

enum class Screen { CALENDAR, ACCOUNTS, ADD_ACCOUNT, TASKS }

/** No account yet → add one (the first launch); otherwise the calendar, the accounts, or adding one. */
@Composable
fun PlanItRoot(viewModel: RootViewModel) {
    val hasAccounts by viewModel.hasAccounts.collectAsStateWithLifecycle()
    when {
        hasAccounts == null -> Unit
        hasAccounts == false -> AddAccountScreen(onAdded = {}, onCancel = null)
        viewModel.screen == Screen.ADD_ACCOUNT -> AddAccountScreen(
            onAdded = { viewModel.screen = Screen.CALENDAR },
            onCancel = { viewModel.screen = Screen.ACCOUNTS },
        )
        viewModel.screen == Screen.ACCOUNTS -> AccountsScreen(
            onBack = { viewModel.screen = Screen.CALENDAR },
            onAddAccount = { viewModel.screen = Screen.ADD_ACCOUNT },
        )
        viewModel.screen == Screen.TASKS -> TasksScreen(onBack = { viewModel.screen = Screen.CALENDAR })
        else -> CalendarScreen(onAccounts = { viewModel.screen = Screen.ACCOUNTS }, onTasks = { viewModel.screen = Screen.TASKS }, openDate = viewModel.openDate)
    }
}
