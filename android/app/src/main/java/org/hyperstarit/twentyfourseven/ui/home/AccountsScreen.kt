package org.hyperstarit.twentyfourseven.ui.home

import android.text.format.DateUtils
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.shape.RoundedCornerShape
import org.hyperstarit.twentyfourseven.ui.theme.Banner
import org.hyperstarit.twentyfourseven.ui.theme.ColorDot
import org.hyperstarit.twentyfourseven.ui.theme.GhostButton
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.IconTile
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import org.hyperstarit.twentyfourseven.ui.theme.TopBar
import org.hyperstarit.twentyfourseven.ui.theme.fieldColors
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.input.clearText
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedSecureTextField
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
import androidx.compose.ui.unit.dp
import androidx.core.graphics.toColorInt
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.io.IOException
import javax.inject.Inject
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.UntrustedCertificateException
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.NoticeEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.net.CertificateInfo
import org.hyperstarit.twentyfourseven.net.UnauthorizedException
import org.hyperstarit.twentyfourseven.sync.LocalChanges
import org.hyperstarit.twentyfourseven.sync.SyncRunner
import org.hyperstarit.twentyfourseven.ui.accounts.CertificateDialog

data class AccountSection(val account: AccountEntity, val calendars: List<Pair<CalendarEntity, Int>>)

data class HomeState(val sections: List<AccountSection> = emptyList(), val notices: List<NoticeEntity> = emptyList(), val pending: Int = 0)

@HiltViewModel
class HomeViewModel @Inject constructor(
    db: AppDatabase,
    private val accounts: Accounts,
    private val runner: SyncRunner,
    private val changes: LocalChanges,
    private val push: org.hyperstarit.twentyfourseven.push.PushRegistrar,
) : ViewModel() {
    val state = combine(
        accounts.all, db.calendars().observeAll(), db.events().observeCounts(), db.notices().observeAll(), db.outbox().observePending(),
    ) { all, calendars, counts, notices, pending ->
        val byCalendar = counts.associate { it.calendarId to it.count }
        HomeState(
            sections = all.map { a -> AccountSection(a, calendars.filter { it.accountId == a.id }.map { it to (byCalendar[it.id] ?: 0) }) },
            notices = notices,
            pending = pending,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), HomeState())

    var syncing by mutableStateOf(false)
        private set
    var message by mutableStateOf<String?>(null)
    var certificateReview by mutableStateOf<Pair<String, CertificateInfo>?>(null)
        private set

    fun syncNow() {
        if (syncing) return
        syncing = true
        viewModelScope.launch {
            try { runner.syncAll() } finally { syncing = false }
        }
    }

    fun signInAgain(account: AccountEntity, password: String, done: (String?) -> Unit) {
        viewModelScope.launch {
            val error = try {
                accounts.signInAgain(account.id, password)
                syncNow()
                null
            } catch (e: UnauthorizedException) {
                e.errors.firstOrNull() ?: "Wrong password."
            } catch (e: UntrustedCertificateException) {
                certificateReview = account.id to e.certificate
                "The server's certificate has changed."
            } catch (e: IOException) {
                e.message ?: "Couldn't reach the server."
            }
            done(error)
        }
    }

    fun reviewCertificate(account: AccountEntity) {
        viewModelScope.launch {
            try {
                certificateReview = account.id to (accounts.presentedCertificate(account.id) ?: return@launch)
            } catch (e: IOException) {
                message = "Couldn't reach the server: ${e.message}"
            }
        }
    }

    fun trustReviewed() {
        val (accountId, info) = certificateReview ?: return
        certificateReview = null
        viewModelScope.launch {
            accounts.trustCertificate(accountId, info.sha256)
            syncNow()
        }
    }

    fun dismissReview() {
        certificateReview = null
    }

    fun remove(account: AccountEntity) {
        viewModelScope.launch {
            push.forget(account.id)
            accounts.remove(account.id)
        }
    }

    fun reapply(notice: NoticeEntity) {
        viewModelScope.launch { changes.reapply(notice.id) }
    }

    fun dismiss(notice: NoticeEntity) {
        viewModelScope.launch { changes.dismiss(notice.id) }
    }
}

/** The accounts, their calendars, and what the user should know about their changes. */
@Composable
fun AccountsScreen(onBack: () -> Unit, onAddAccount: () -> Unit, viewModel: HomeViewModel = hiltViewModel()) {
    androidx.activity.compose.BackHandler(onBack = onBack)
    val c = Brand.colors
    val state by viewModel.state.collectAsStateWithLifecycle()
    var signingIn by remember { mutableStateOf<AccountEntity?>(null) }
    var removing by remember { mutableStateOf<AccountEntity?>(null) }

    Scaffold(
        containerColor = Color.Transparent,
        topBar = {
            Column {
                TopBar(
                    "Accounts",
                    navigation = { RoundIconButton(Glyphs.back, "Back", onBack) },
                    actions = { RoundIconButton(Glyphs.sync, "Sync now", viewModel::syncNow) },
                )
                if (viewModel.syncing) LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp), color = c.highlight, trackColor = Color.Transparent)
            }
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding),
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (state.pending > 0) {
                item { Banner(if (state.pending == 1) "1 change waiting to be sent" else "${state.pending} changes waiting to be sent", warning = false) }
            }
            viewModel.message?.let { item { Banner(it) } }
            items(state.notices, key = { "n${it.id}" }) { notice ->
                NoticeCard(notice, onReapply = { viewModel.reapply(notice) }, onDismiss = { viewModel.dismiss(notice) })
            }
            items(state.sections, key = { "a${it.account.id}" }) { section ->
                AccountCard(
                    section,
                    onSignIn = { signingIn = section.account },
                    onReviewCertificate = { viewModel.reviewCertificate(section.account) },
                    onRemove = { removing = section.account },
                )
            }
            item {
                GhostButton("Add another account", onAddAccount, Modifier.fillMaxWidth().padding(top = 6.dp), icon = Glyphs.plus)
            }
        }
    }

    signingIn?.let { account ->
        SignInAgainDialog(account, onDone = { signingIn = null }, viewModel = viewModel)
    }
    removing?.let { account ->
        AlertDialog(
            onDismissRequest = { removing = null },
            icon = { Icon(Glyphs.alert, null, tint = c.dangerInk) },
            title = { Text("Remove this account?") },
            text = { Text("${account.username} is signed out and its calendars are removed from this phone. Nothing is deleted on the server.", color = c.textDim) },
            confirmButton = { TextAction("Remove", { viewModel.remove(account); removing = null }, color = c.dangerInk) },
            dismissButton = { TextAction("Cancel", { removing = null }, color = c.textDim) },
        )
    }
    viewModel.certificateReview?.let { (_, info) ->
        CertificateDialog(info, changed = true, onTrust = viewModel::trustReviewed, onCancel = viewModel::dismissReview)
    }
}

@Composable
private fun AccountCard(section: AccountSection, onSignIn: () -> Unit, onReviewCertificate: () -> Unit, onRemove: () -> Unit) {
    val c = Brand.colors
    val account = section.account
    var menu by remember { mutableStateOf(false) }
    val status = when {
        account.certificateChanged -> "The server's certificate has changed."
        account.needsSignIn -> "Signed out by the server."
        account.lastError != null -> account.lastError
        account.lastSyncAt != null -> "Synced " + DateUtils.getRelativeTimeSpanString(account.lastSyncAt)
        else -> "Not synced yet"
    }
    val problem = account.certificateChanged || account.needsSignIn || account.lastError != null
    Panel(padding = 0.dp, border = if (problem) c.danger.copy(alpha = 0.4f) else c.hairline) {
        Row(Modifier.fillMaxWidth().padding(start = 16.dp, top = 14.dp, end = 6.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            IconTile(Glyphs.server)
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text(account.username, style = MaterialTheme.typography.titleMedium, color = c.text)
                Text(
                    account.server + if (account.certificatePin != null) " · own certificate" else "",
                    style = MaterialTheme.typography.bodySmall, color = c.textDim,
                )
            }
            Box {
                RoundIconButton(Glyphs.more, "Account options", { menu = true }, filled = false)
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }, containerColor = c.surface2, shape = RoundedCornerShape(14.dp)) {
                    DropdownMenuItem(
                        text = { Text("Remove account", color = c.dangerInk) },
                        leadingIcon = { Icon(Glyphs.trash, null, tint = c.dangerInk, modifier = Modifier.size(18.dp)) },
                        onClick = { menu = false; onRemove() },
                    )
                }
            }
        }
        Row(Modifier.padding(start = 16.dp, end = 16.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).clip(CircleShape).background(if (problem) c.danger else c.highlight))
            Text(status, style = MaterialTheme.typography.bodySmall, color = if (problem) c.dangerInk else c.textDim, modifier = Modifier.padding(start = 8.dp))
        }
        if (account.needsSignIn) PrimaryButton("Sign in again", onSignIn, Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 12.dp))
        if (account.certificateChanged) GhostButton("Review certificate", onReviewCertificate, Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 12.dp), icon = Glyphs.shield)
        if (section.calendars.isNotEmpty()) {
            Hairline()
            SectionLabel("Calendars", Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp))
            section.calendars.forEach { (calendar, count) ->
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    ColorDot(calendar.color?.let(::parseColor) ?: c.accent, size = 12.dp)
                    Text(calendar.name, style = MaterialTheme.typography.bodyLarge, color = c.text, modifier = Modifier.weight(1f).padding(horizontal = 12.dp))
                    Text(
                        (if (count == 1) "1 event" else "$count events") + if (calendar.readOnly) " · read-only" else "",
                        style = MaterialTheme.typography.bodySmall, color = c.textFaint,
                    )
                }
            }
            Spacer(Modifier.height(8.dp))
        }
    }
}

@Composable
private fun NoticeCard(notice: NoticeEntity, onReapply: () -> Unit, onDismiss: () -> Unit) {
    val c = Brand.colors
    Panel(border = c.danger.copy(alpha = 0.35f)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(Glyphs.alert, null, tint = c.dangerInk, modifier = Modifier.size(18.dp))
            Text("“${notice.title}” wasn't saved", style = MaterialTheme.typography.titleSmall, color = c.text, modifier = Modifier.padding(start = 10.dp))
        }
        Text(notice.message, style = MaterialTheme.typography.bodySmall, color = c.textDim, modifier = Modifier.padding(top = 6.dp))
        Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.End) {
            if (notice.op != null) TextAction("Apply mine anyway", onReapply)
            TextAction("OK", onDismiss, color = c.textDim)
        }
    }
}

@Composable
private fun SignInAgainDialog(account: AccountEntity, onDone: () -> Unit, viewModel: HomeViewModel) {
    val password = remember { TextFieldState() }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = onDone,
        title = { Text("Sign in again") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("${account.username} on ${account.server}", color = Brand.colors.textDim)
                OutlinedSecureTextField(state = password, label = { Text("Password") }, shape = RoundedCornerShape(12.dp), colors = fieldColors())
                error?.let { Text(it, color = Brand.colors.dangerInk) }
            }
        },
        confirmButton = {
            TextAction("Sign in", enabled = !busy, onClick = {
                busy = true
                viewModel.signInAgain(account, password.text.toString()) { e ->
                    busy = false
                    if (e == null) { password.clearText(); onDone() } else error = e
                }
            })
        },
        dismissButton = { TextAction("Cancel", onDone, color = Brand.colors.textDim) },
    )
}

private fun parseColor(hex: String): Color? = runCatching { Color(hex.toColorInt()) }.getOrNull()
