package org.hyperstarit.planit.ui.home

import android.text.format.DateUtils
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.OutlinedSecureTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
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
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.accounts.UntrustedCertificateException
import org.hyperstarit.planit.data.db.AccountEntity
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.NoticeEntity
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.net.CertificateInfo
import org.hyperstarit.planit.net.UnauthorizedException
import org.hyperstarit.planit.sync.LocalChanges
import org.hyperstarit.planit.sync.SyncRunner
import org.hyperstarit.planit.ui.accounts.CertificateDialog

data class AccountSection(val account: AccountEntity, val calendars: List<Pair<CalendarEntity, Int>>)

data class HomeState(val sections: List<AccountSection> = emptyList(), val notices: List<NoticeEntity> = emptyList(), val pending: Int = 0)

@HiltViewModel
class HomeViewModel @Inject constructor(
    db: PlanItDatabase,
    private val accounts: Accounts,
    private val runner: SyncRunner,
    private val changes: LocalChanges,
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
        viewModelScope.launch { accounts.remove(account.id) }
    }

    fun reapply(notice: NoticeEntity) {
        viewModelScope.launch { changes.reapply(notice.id) }
    }

    fun dismiss(notice: NoticeEntity) {
        viewModelScope.launch { changes.dismiss(notice.id) }
    }
}

/** The accounts, their calendars, and what the user should know about their changes. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AccountsScreen(onBack: () -> Unit, onAddAccount: () -> Unit, viewModel: HomeViewModel = hiltViewModel()) {
    androidx.activity.compose.BackHandler(onBack = onBack)
    val state by viewModel.state.collectAsStateWithLifecycle()
    var signingIn by remember { mutableStateOf<AccountEntity?>(null) }
    var removing by remember { mutableStateOf<AccountEntity?>(null) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Accounts") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(androidx.compose.material.icons.Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    IconButton(onClick = viewModel::syncNow) { Icon(Icons.Default.Refresh, contentDescription = "Sync now") }
                    IconButton(onClick = onAddAccount) { Icon(Icons.Default.Add, contentDescription = "Add account") }
                },
            )
        },
    ) { padding ->
        LazyColumn(modifier = Modifier.fillMaxSize().padding(padding)) {
            item {
                if (viewModel.syncing) LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                if (state.pending > 0) {
                    Text(
                        "${state.pending} change(s) waiting to be sent",
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                    )
                }
                viewModel.message?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            }
            items(state.notices, key = { "n${it.id}" }) { notice ->
                NoticeCard(notice, onReapply = { viewModel.reapply(notice) }, onDismiss = { viewModel.dismiss(notice) })
            }
            state.sections.forEach { section ->
                item(key = "a${section.account.id}") {
                    AccountHeader(
                        section.account,
                        onSignIn = { signingIn = section.account },
                        onReviewCertificate = { viewModel.reviewCertificate(section.account) },
                        onRemove = { removing = section.account },
                    )
                }
                items(section.calendars, key = { "c${it.first.id}" }) { (calendar, count) ->
                    ListItem(
                        leadingContent = {
                            Box(
                                Modifier.size(14.dp).clip(CircleShape)
                                    .background(calendar.color?.let(::parseColor) ?: MaterialTheme.colorScheme.primary),
                            )
                        },
                        supportingContent = {
                            Text(if (calendar.readOnly) "$count events · subscription, read-only" else "$count events")
                        },
                    ) { Text(calendar.name) }
                }
                item(key = "d${section.account.id}") { HorizontalDivider() }
            }
        }
    }

    signingIn?.let { account ->
        SignInAgainDialog(account, onDone = { signingIn = null }, viewModel = viewModel)
    }
    removing?.let { account ->
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text("Remove account?") },
            text = { Text("${account.username} is signed out and its calendars are removed from this device. Nothing is deleted on the server.") },
            confirmButton = { TextButton(onClick = { viewModel.remove(account); removing = null }) { Text("Remove") } },
            dismissButton = { TextButton(onClick = { removing = null }) { Text("Cancel") } },
        )
    }
    viewModel.certificateReview?.let { (_, info) ->
        CertificateDialog(info, changed = true, onTrust = viewModel::trustReviewed, onCancel = viewModel::dismissReview)
    }
}

@Composable
private fun AccountHeader(account: AccountEntity, onSignIn: () -> Unit, onReviewCertificate: () -> Unit, onRemove: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    val status = when {
        account.certificateChanged -> "The server's certificate has changed."
        account.needsSignIn -> "Signed out by the server."
        account.lastError != null -> account.lastError
        account.lastSyncAt != null -> "Synced " + DateUtils.getRelativeTimeSpanString(account.lastSyncAt)
        else -> "Not synced yet"
    }
    val problem = account.certificateChanged || account.needsSignIn || account.lastError != null
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, top = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(account.username, style = MaterialTheme.typography.titleMedium)
            Text(account.server + if (account.certificatePin != null) " · own certificate" else "", style = MaterialTheme.typography.bodySmall)
            Text(status, style = MaterialTheme.typography.bodySmall, color = if (problem) MaterialTheme.colorScheme.error else Color.Unspecified)
            if (account.needsSignIn) TextButton(onClick = onSignIn) { Text("Sign in again") }
            if (account.certificateChanged) TextButton(onClick = onReviewCertificate) { Text("Review certificate") }
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "Account options") }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text("Remove account") }, onClick = { menu = false; onRemove() })
            }
        }
    }
}

@Composable
private fun NoticeCard(notice: NoticeEntity, onReapply: () -> Unit, onDismiss: () -> Unit) {
    OutlinedCard(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("“${notice.title}” wasn't saved", style = MaterialTheme.typography.titleSmall)
            Text(notice.message, style = MaterialTheme.typography.bodySmall)
            Row {
                if (notice.op != null) TextButton(onClick = onReapply) { Text("Apply mine anyway") }
                TextButton(onClick = onDismiss) { Text("OK") }
            }
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
                Text("${account.username} on ${account.server}")
                OutlinedSecureTextField(state = password, label = { Text("Password") })
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = {
                busy = true
                viewModel.signInAgain(account, password.text.toString()) { e ->
                    busy = false
                    if (e == null) { password.clearText(); onDone() } else error = e
                }
            }) { Text("Sign in") }
        },
        dismissButton = { TextButton(onClick = onDone) { Text("Cancel") } },
    )
}

private fun parseColor(hex: String): Color? = runCatching { Color(hex.toColorInt()) }.getOrNull()
