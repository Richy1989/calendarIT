package org.hyperstarit.planit.ui.login

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.input.clearText
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedSecureTextField
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.autofill.ContentType
import androidx.compose.ui.semantics.contentType
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.io.IOException
import javax.inject.Inject
import kotlinx.coroutines.launch
import org.hyperstarit.planit.BuildConfig
import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.net.ServerUrl
import org.hyperstarit.planit.net.UnauthorizedException

@HiltViewModel
class LoginViewModel @Inject constructor(private val session: SessionManager) : ViewModel() {
    var server by mutableStateOf("")
    var email by mutableStateOf("")
    val password = TextFieldState()
    var busy by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    fun submit() {
        if (busy) return
        val url = when (val parsed = ServerUrl.parse(server, allowCleartext = BuildConfig.DEBUG)) {
            is ServerUrl.Result.Invalid -> { error = parsed.message; return }
            is ServerUrl.Result.Ok -> parsed.url
        }
        if (email.isBlank() || password.text.isEmpty()) {
            error = "Enter your email and password."
            return
        }
        busy = true
        error = null
        viewModelScope.launch {
            try {
                session.login(url, email, password.text.toString())
                password.clearText()
            } catch (e: UnauthorizedException) {
                error = e.errors.firstOrNull() ?: "Wrong email or password."
            } catch (e: IOException) {
                error = e.message?.takeIf { it.isNotBlank() } ?: "Couldn't reach the server."
            } finally {
                busy = false
            }
        }
    }
}

@Composable
fun LoginScreen(notice: String?, viewModel: LoginViewModel = hiltViewModel()) {
    Scaffold { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .safeDrawingPadding()
                .imePadding()
                .verticalScroll(rememberScrollState())
                .padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text("PlanIt", style = MaterialTheme.typography.headlineLarge)
            Text("Sign in to your CalendarIT server.", style = MaterialTheme.typography.bodyLarge)
            if (notice != null && viewModel.error == null) {
                Text(notice, color = MaterialTheme.colorScheme.primary)
            }
            OutlinedTextField(
                value = viewModel.server,
                onValueChange = { viewModel.server = it },
                label = { Text("Server") },
                placeholder = { Text("calendar.example.com") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = viewModel.email,
                onValueChange = { viewModel.email = it },
                label = { Text("Email") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next),
                modifier = Modifier.fillMaxWidth().semantics { contentType = ContentType.EmailAddress + ContentType.Username },
            )
            OutlinedSecureTextField(
                state = viewModel.password,
                label = { Text("Password") },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                onKeyboardAction = { viewModel.submit() },
                modifier = Modifier.fillMaxWidth().semantics { contentType = ContentType.Password },
            )
            viewModel.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            Button(onClick = viewModel::submit, enabled = !viewModel.busy, modifier = Modifier.fillMaxWidth()) {
                if (viewModel.busy) {
                    CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp)
                } else {
                    Text("Sign in")
                }
            }
        }
    }
}
