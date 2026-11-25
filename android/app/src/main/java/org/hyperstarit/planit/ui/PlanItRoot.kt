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
import org.hyperstarit.planit.ui.home.HomeScreen

@HiltViewModel
class RootViewModel @Inject constructor(accounts: Accounts) : ViewModel() {
    /** Null until the accounts are read (the splash stays up meanwhile). */
    val hasAccounts = accounts.all.map { it.isNotEmpty() }.stateIn(viewModelScope, SharingStarted.Eagerly, null)
    var addingAccount by mutableStateOf(false)
}

/** No account yet → add one (the first launch); otherwise the app, with "add account" on top when asked. */
@Composable
fun PlanItRoot(viewModel: RootViewModel) {
    val hasAccounts by viewModel.hasAccounts.collectAsStateWithLifecycle()
    when {
        hasAccounts == null -> Unit
        hasAccounts == false -> AddAccountScreen(onAdded = {}, onCancel = null)
        viewModel.addingAccount -> AddAccountScreen(
            onAdded = { viewModel.addingAccount = false },
            onCancel = { viewModel.addingAccount = false },
        )
        else -> HomeScreen(onAddAccount = { viewModel.addingAccount = true })
    }
}
