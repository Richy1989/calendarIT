package org.hyperstarit.planit.ui.accounts

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.input.clearText
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedSecureTextField
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
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
import okhttp3.HttpUrl
import org.hyperstarit.planit.BuildConfig
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.accounts.UntrustedCertificateException
import org.hyperstarit.planit.net.CertificateInfo
import org.hyperstarit.planit.net.ServerUrl
import org.hyperstarit.planit.net.UnauthorizedException
import org.hyperstarit.planit.sync.SyncScheduler

enum class AddStep { CHOOSE, PLANIT }

@HiltViewModel
class AddAccountViewModel @Inject constructor(
    private val accounts: Accounts,
    private val scheduler: SyncScheduler,
) : ViewModel() {
    var step by mutableStateOf(AddStep.CHOOSE)
    var server by mutableStateOf("")
    var email by mutableStateOf("")
    val password = TextFieldState()
    var busy by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    /** A certificate waiting for the user's decision. */
    var certificate by mutableStateOf<CertificateInfo?>(null)
        private set
    private var pendingUrl: HttpUrl? = null

    fun submit(onAdded: () -> Unit) {
        if (busy) return
        val url = when (val parsed = ServerUrl.parse(server, allowCleartext = BuildConfig.DEBUG)) {
            is ServerUrl.Result.Invalid -> { error = parsed.message; return }
            is ServerUrl.Result.Ok -> parsed.url
        }
        if (email.isBlank() || password.text.isEmpty()) {
            error = "Enter your email and password."
            return
        }
        signIn(url, pin = null, onAdded)
    }

    fun trustCertificate(onAdded: () -> Unit) {
        val info = certificate ?: return
        val url = pendingUrl ?: return
        certificate = null
        signIn(url, pin = info.sha256, onAdded)
    }

    fun rejectCertificate() {
        certificate = null
        error = "Not signed in: the server's certificate wasn't trusted."
    }

    private fun signIn(url: HttpUrl, pin: String?, onAdded: () -> Unit) {
        busy = true
        error = null
        viewModelScope.launch {
            try {
                accounts.addPlanIt(url, email, password.text.toString(), pin)
                password.clearText()
                scheduler.requestNow()
                onAdded()
            } catch (e: UntrustedCertificateException) {
                pendingUrl = url
                certificate = e.certificate
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

/** Adding an account: first which kind, then its sign-in. [onCancel] is null for the first account. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddAccountScreen(onAdded: () -> Unit, onCancel: (() -> Unit)?, viewModel: AddAccountViewModel = hiltViewModel()) {
    val back: (() -> Unit)? = when {
        viewModel.step == AddStep.PLANIT -> ({ viewModel.step = AddStep.CHOOSE })
        else -> onCancel
    }
    BackHandler(enabled = back != null) { back?.invoke() }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(if (viewModel.step == AddStep.CHOOSE) "Add an account" else "PlanIT server") },
                navigationIcon = {
                    if (back != null) {
                        IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back") }
                    }
                },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .imePadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            when (viewModel.step) {
                AddStep.CHOOSE -> ChooseKind(onPlanIt = { viewModel.step = AddStep.PLANIT })
                AddStep.PLANIT -> PlanItForm(viewModel, onAdded)
            }
        }
    }
    viewModel.certificate?.let { info ->
        CertificateDialog(info, changed = false, onTrust = { viewModel.trustCertificate(onAdded) }, onCancel = viewModel::rejectCertificate)
    }
}

@Composable
private fun ChooseKind(onPlanIt: () -> Unit) {
    Text("Where are your calendars?", style = MaterialTheme.typography.titleMedium)
    Card(onClick = onPlanIt, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("PlanIT server", style = MaterialTheme.typography.titleMedium)
            Text(
                "Your own PlanIT backend, at home or anywhere. Everything works offline and syncs " +
                    "in the background, with categories, invitations and instant updates.",
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
    Card(onClick = {}, enabled = false, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("CalDAV server", style = MaterialTheme.typography.titleMedium)
            Text(
                "Nextcloud, Radicale, Baïkal, iCloud and other CalDAV servers. Coming in a later version.",
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
}

@Composable
private fun PlanItForm(viewModel: AddAccountViewModel, onAdded: () -> Unit) {
    Text("Sign in to your PlanIT server.", style = MaterialTheme.typography.bodyLarge)
    OutlinedTextField(
        value = viewModel.server,
        onValueChange = { viewModel.server = it },
        label = { Text("Server address") },
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
        onKeyboardAction = { viewModel.submit(onAdded) },
        modifier = Modifier.fillMaxWidth().semantics { contentType = ContentType.Password },
    )
    viewModel.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    Button(onClick = { viewModel.submit(onAdded) }, enabled = !viewModel.busy, modifier = Modifier.fillMaxWidth()) {
        if (viewModel.busy) CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp) else Text("Sign in")
    }
}
