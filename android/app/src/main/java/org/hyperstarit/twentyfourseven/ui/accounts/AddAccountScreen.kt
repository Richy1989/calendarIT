package org.hyperstarit.twentyfourseven.ui.accounts

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.sp
import org.hyperstarit.twentyfourseven.ui.theme.Banner
import org.hyperstarit.twentyfourseven.ui.theme.BrandMark
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.IconTile
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.BrandTextField
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.TopBar
import org.hyperstarit.twentyfourseven.ui.theme.fieldColors
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
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedSecureTextField
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
import okhttp3.HttpUrl
import org.hyperstarit.twentyfourseven.BuildConfig
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.UntrustedCertificateException
import org.hyperstarit.twentyfourseven.net.CertificateInfo
import org.hyperstarit.twentyfourseven.net.ServerUrl
import org.hyperstarit.twentyfourseven.net.UnauthorizedException
import org.hyperstarit.twentyfourseven.sync.SyncScheduler

enum class AddStep { CHOOSE, CALENDARIT, CALDAV }

@HiltViewModel
class AddAccountViewModel @Inject constructor(
    private val accounts: Accounts,
    private val scheduler: SyncScheduler,
    private val push: org.hyperstarit.twentyfourseven.push.PushRegistrar,
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
            error = if (step == AddStep.CALDAV) "Enter your username and password." else "Enter your email and password."
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
                if (step == AddStep.CALDAV) {
                    accounts.addCalDav(url, email, password.text.toString(), pin)
                } else {
                    accounts.addCalendarIt(url, email, password.text.toString(), pin)
                }
                password.clearText()
                scheduler.requestNow()
                if (step == AddStep.CALENDARIT && push.current() != null) push.registerAll()
                onAdded()
            } catch (e: UntrustedCertificateException) {
                pendingUrl = url
                certificate = e.certificate
            } catch (e: UnauthorizedException) {
                error = e.errors.firstOrNull() ?: "Wrong email or password."
            } catch (_: org.hyperstarit.twentyfourseven.caldav.DavAuthException) {
                error = "Wrong username or password."
            } catch (e: IOException) {
                error = e.message?.takeIf { it.isNotBlank() } ?: "Couldn't reach the server."
            } finally {
                busy = false
            }
        }
    }
}

/** Adding an account: first which kind, then its sign-in. [onCancel] is null for the first account. */
@Composable
fun AddAccountScreen(onAdded: () -> Unit, onCancel: (() -> Unit)?, viewModel: AddAccountViewModel = hiltViewModel()) {
    val back: (() -> Unit)? = when {
        viewModel.step != AddStep.CHOOSE -> ({ viewModel.step = AddStep.CHOOSE })
        else -> onCancel
    }
    BackHandler(enabled = back != null) { back?.invoke() }
    Scaffold(
        containerColor = Color.Transparent,
        topBar = {
            TopBar(
                "",
                navigation = if (back != null) { { RoundIconButton(Glyphs.back, "Back", back) } } else null,
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
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            when (viewModel.step) {
                AddStep.CHOOSE -> ChooseKind(
                    first = onCancel == null,
                    onCalendarIt = { viewModel.step = AddStep.CALENDARIT },
                    onCalDav = { viewModel.step = AddStep.CALDAV },
                )
                AddStep.CALENDARIT -> SignInForm(
                    viewModel, onAdded, "Use the address of your 24seven server and the account you use on the web.",
                    placeholder = "calendar.example.com", userLabel = "Email", email = true,
                )
                AddStep.CALDAV -> SignInForm(
                    viewModel, onAdded,
                    "The address of your CalDAV server (or of one of its calendars) and your login. " +
                        "For iCloud use caldav.icloud.com and an app-specific password; Nextcloud works best with an app password too.",
                    placeholder = "cloud.example.com/remote.php/dav", userLabel = "Username", email = false,
                )
            }
        }
    }
    viewModel.certificate?.let { info ->
        CertificateDialog(info, changed = false, onTrust = { viewModel.trustCertificate(onAdded) }, onCancel = viewModel::rejectCertificate)
    }
}

@Composable
private fun Hero(title: String, body: String) {
    val c = Brand.colors
    Column(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        BrandMark(
            96.dp,
            Modifier.shadow(28.dp, RoundedCornerShape(22.dp), ambientColor = c.accent, spotColor = c.accent.copy(alpha = 0.6f * c.glow)),
        )
        Spacer(Modifier.height(22.dp))
        Text(title, style = MaterialTheme.typography.displaySmall, fontSize = 32.sp, color = c.text, textAlign = TextAlign.Center)
        Spacer(Modifier.height(8.dp))
        Text(body, style = MaterialTheme.typography.bodyLarge, color = c.textDim, textAlign = TextAlign.Center)
    }
}

@Composable
private fun ChooseKind(first: Boolean, onCalendarIt: () -> Unit, onCalDav: () -> Unit) {
    val c = Brand.colors
    Hero(
        if (first) "Welcome to 24seven" else "Add an account",
        "Your calendar and tasks, on your own server — and on this phone, even offline.",
    )
    SectionLabel("Where are your calendars?", Modifier.padding(start = 4.dp, top = 8.dp))
    Panel(onClick = onCalendarIt, border = c.violet.copy(alpha = 0.45f)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconTile(Glyphs.server, background = c.accent.copy(alpha = 0.14f))
            Column(Modifier.weight(1f).padding(horizontal = 14.dp)) {
                Text("24seven server", style = MaterialTheme.typography.titleMedium, color = c.text)
                Text(
                    "Your own 24seven server, at home or anywhere. Works offline, syncs in the background, with categories, invitations and instant updates.",
                    style = MaterialTheme.typography.bodySmall, color = c.textDim, modifier = Modifier.padding(top = 2.dp),
                )
            }
            Icon(Glyphs.chevronRight, null, tint = c.textFaint, modifier = Modifier.size(20.dp))
        }
    }
    Panel(onClick = onCalDav) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconTile(Glyphs.globe, background = c.accent.copy(alpha = 0.14f))
            Column(Modifier.weight(1f).padding(horizontal = 14.dp)) {
                Text("CalDAV server", style = MaterialTheme.typography.titleMedium, color = c.text)
                Text(
                    "Nextcloud, Radicale, Baïkal, iCloud and others. Events, tasks and reminders; syncs every 15 minutes and after each change.",
                    style = MaterialTheme.typography.bodySmall, color = c.textDim, modifier = Modifier.padding(top = 2.dp),
                )
            }
            Icon(Glyphs.chevronRight, null, tint = c.textFaint, modifier = Modifier.size(20.dp))
        }
    }
}

@Composable
private fun SignInForm(
    viewModel: AddAccountViewModel, onAdded: () -> Unit, intro: String, placeholder: String, userLabel: String, email: Boolean,
) {
    val c = Brand.colors
    Hero("Sign in", intro)
    BrandTextField(
        viewModel.server, { viewModel.server = it }, "Server address", placeholder = placeholder,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
    )
    BrandTextField(
        viewModel.email, { viewModel.email = it }, userLabel,
        modifier = Modifier.semantics { contentType = if (email) ContentType.EmailAddress + ContentType.Username else ContentType.Username },
        keyboardOptions = KeyboardOptions(keyboardType = if (email) KeyboardType.Email else KeyboardType.Text, imeAction = ImeAction.Next),
    )
    OutlinedSecureTextField(
        state = viewModel.password,
        label = { Text("Password") },
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
        onKeyboardAction = { viewModel.submit(onAdded) },
        shape = RoundedCornerShape(12.dp),
        colors = fieldColors(),
        modifier = Modifier.fillMaxWidth().semantics { contentType = ContentType.Password },
    )
    viewModel.error?.let { Banner(it) }
    PrimaryButton("Sign in", { viewModel.submit(onAdded) }, Modifier.fillMaxWidth().padding(top = 6.dp), loading = viewModel.busy)
    Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
        Icon(Glyphs.shield, null, tint = c.textFaint, modifier = Modifier.size(14.dp))
        Text(
            "Your password is only sent to your server.", style = MaterialTheme.typography.bodySmall, color = c.textFaint,
            modifier = Modifier.padding(start = 6.dp),
        )
    }
}
