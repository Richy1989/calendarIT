package org.hyperstarit.planit.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import org.hyperstarit.planit.auth.AuthState
import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.ui.home.HomeScreen
import org.hyperstarit.planit.ui.login.LoginScreen

@HiltViewModel
class RootViewModel @Inject constructor(session: SessionManager) : ViewModel() {
    val state = session.state
}

/** Signed out → the login; signed in → the app. */
@Composable
fun PlanItRoot(viewModel: RootViewModel = hiltViewModel()) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    when (val s = state) {
        AuthState.Loading -> Unit // the splash screen is still up
        is AuthState.SignedOut -> LoginScreen(notice = s.reason)
        is AuthState.SignedIn -> HomeScreen(server = s.server, email = s.email)
    }
}
