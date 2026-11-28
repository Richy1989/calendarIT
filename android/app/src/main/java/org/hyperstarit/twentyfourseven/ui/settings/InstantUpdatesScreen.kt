package org.hyperstarit.twentyfourseven.ui.settings

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.text.format.DateUtils
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AccountKind
import org.hyperstarit.twentyfourseven.push.PushPrefs
import org.hyperstarit.twentyfourseven.push.PushRegistrar
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.GhostButton
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import org.hyperstarit.twentyfourseven.ui.theme.TopBar

/** Where a test push is. */
sealed interface TestState {
    data object Sending : TestState
    data class Waiting(val since: Long) : TestState
    data class Arrived(val afterMs: Long) : TestState
    data class Failed(val message: String) : TestState
}

@HiltViewModel
class InstantUpdatesViewModel @Inject constructor(
    private val appScope: CoroutineScope,
    private val accounts: Accounts,
    private val push: PushRegistrar,
    private val prefs: PushPrefs,
) : ViewModel() {
    /** The 24seven accounts (CalDAV accounts have no server of ours to push). */
    val calendarItAccounts = accounts.all.map { all -> all.filter { it.kind == AccountKind.CALENDARIT } }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    var distributors by mutableStateOf(push.distributors())
        private set
    var current by mutableStateOf(push.current())
        private set

    /** Bumped whenever the push prefs change (an endpoint arrived, a push came in). */
    var revision by mutableIntStateOf(0)
        private set
    val tests = mutableStateMapOf<String, TestState>()

    private val stopObserving = prefs.observe { revision++; checkArrivals() }
    private val timeouts = mutableMapOf<String, Job>()

    fun refresh() {
        distributors = push.distributors()
        current = push.current()
        revision++
    }

    fun hasEndpoint(accountId: String): Boolean = revision.let { prefs.endpoint(accountId) != null }
    fun lastPush(accountId: String): Long? = revision.let { prefs.lastPush(accountId) }

    /** Picks the distributor (asking when there are several) and registers every account with it. */
    fun turnOn(activity: android.app.Activity) {
        org.unifiedpush.android.connector.UnifiedPush.tryPickDistributor(activity) { ok ->
            if (ok) appScope.launch { push.turnOn(); refresh() } else refresh()
        }
    }

    fun turnOff() {
        appScope.launch {
            push.unregisterAll()
            tests.clear()
            refresh()
        }
    }

    fun test(account: AccountEntity) {
        tests[account.id] = TestState.Sending
        viewModelScope.launch {
            val since = System.currentTimeMillis()
            val answer = runCatching { accounts.testPush(account) }
            answer.onFailure { tests[account.id] = TestState.Failed(it.message ?: "Couldn't reach the server."); return@launch }
            val result = answer.getOrThrow()
            if (result.result != "sent") {
                tests[account.id] = TestState.Failed(result.message)
                refresh()
                return@launch
            }
            tests[account.id] = TestState.Waiting(since)
            checkArrivals()
            timeouts.remove(account.id)?.cancel()
            timeouts[account.id] = launch {
                delay(TEST_TIMEOUT_MS)
                if (tests[account.id] is TestState.Waiting) {
                    tests[account.id] = TestState.Failed(
                        "The server sent it, but it didn't arrive. Open your UnifiedPush app and check that it is connected, " +
                            "and that battery saving doesn't restrict it or 24seven.",
                    )
                }
            }
        }
    }

    private fun checkArrivals() {
        for ((id, state) in tests.toMap()) {
            if (state !is TestState.Waiting) continue
            val at = prefs.lastTest(id) ?: continue
            if (at >= state.since) {
                tests[id] = TestState.Arrived(at - state.since)
                timeouts.remove(id)?.cancel()
            }
        }
    }

    override fun onCleared() = stopObserving()

    private companion object {
        const val TEST_TIMEOUT_MS = 30_000L
    }
}

/** Settings → Instant updates: what it is, a checklist to set it up, and a test push per account. */
@Composable
fun InstantUpdatesScreen(onBack: () -> Unit, viewModel: InstantUpdatesViewModel = hiltViewModel()) {
    androidx.activity.compose.BackHandler(onBack = onBack)
    val context = LocalContext.current
    val c = Brand.colors
    val accounts by viewModel.calendarItAccounts.collectAsState()
    // Back from installing or opening the distributor app.
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { viewModel.refresh() }

    val installed = viewModel.distributors.isNotEmpty()
    val on = viewModel.current != null
    val connected = on && accounts.isNotEmpty() && accounts.all { viewModel.hasEndpoint(it.id) }
    val distributorName = viewModel.current?.let { appLabel(context, it) } ?: viewModel.distributors.firstOrNull()?.let { appLabel(context, it) }

    Scaffold(
        containerColor = Color.Transparent,
        topBar = { TopBar("Instant updates", navigation = { RoundIconButton(Glyphs.back, "Back", onBack) }) },
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Panel {
                Text(
                    "When something changes on the web or another device, your server sends this phone a tiny “sync now” " +
                        "signal — no calendar content — and 24seven syncs within seconds.",
                    style = MaterialTheme.typography.bodyMedium, color = c.text,
                )
                Text(
                    "The signal goes through a UnifiedPush app such as ntfy, which keeps one connection open for all apps that use it " +
                        "(no Google services needed). Without it, changes arrive within 15 minutes or when you open 24seven.",
                    style = MaterialTheme.typography.bodySmall, color = c.textDim, modifier = Modifier.padding(top = 8.dp),
                )
            }

            SectionLabel("Set up", Modifier.padding(start = 4.dp, top = 14.dp))
            Step(1, "Install a UnifiedPush app", done = installed,
                body = if (installed) "Installed: ${viewModel.distributors.joinToString { appLabel(context, it) }}."
                else "ntfy is free and works out of the box (it uses ntfy.sh unless you change it).") {
                if (!installed) {
                    PrimaryButton("Get ntfy", onClick = { openStore(context) }, icon = Glyphs.plus)
                    TextAction("Other UnifiedPush apps", onClick = { context.open("https://unifiedpush.org/users/distributors/") })
                }
            }
            Step(2, "Turn on instant updates", done = connected, enabled = installed,
                body = when {
                    !installed -> "After step 1."
                    connected -> "On, through $distributorName."
                    on -> "Waiting for $distributorName to hand out an address. Open it once so it connects, then come back."
                    else -> "Registers your 24seven accounts with $distributorName."
                }) {
                if (installed && !on) {
                    PrimaryButton("Turn on", onClick = { (context as? android.app.Activity)?.let(viewModel::turnOn) }, icon = Glyphs.zap)
                } else if (on && !connected) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        viewModel.current?.let { pkg -> GhostButton("Open $distributorName", onClick = { context.launchApp(pkg) }) }
                        GhostButton("Try again", onClick = { (context as? android.app.Activity)?.let(viewModel::turnOn) })
                    }
                }
                if (installed) {
                    Text(
                        "Using your own ntfy server? It must be reachable from the internet: 24seven servers only push to public addresses.",
                        style = MaterialTheme.typography.bodySmall, color = c.textFaint,
                    )
                }
            }
            Step(3, "Check that it works", done = viewModel.tests.values.any { it is TestState.Arrived }, enabled = connected,
                body = if (accounts.isEmpty()) "Instant updates are for 24seven accounts; CalDAV accounts sync on their schedule."
                else "Your server sends a test push; it should arrive within a few seconds.") {
                if (connected) {
                    accounts.forEach { account -> TestRow(account, viewModel) }
                }
            }

            if (on) {
                TextAction("Turn instant updates off", onClick = viewModel::turnOff, color = c.dangerInk, modifier = Modifier.padding(start = 4.dp, top = 8.dp))
            }
        }
    }
}

@Composable
private fun Step(
    number: Int,
    title: String,
    done: Boolean,
    body: String,
    enabled: Boolean = true,
    actions: @Composable () -> Unit,
) {
    val c = Brand.colors
    Panel {
        Row(verticalAlignment = Alignment.Top) {
            Box(
                Modifier.size(28.dp).then(
                    if (done) Modifier.background(c.grad, CircleShape) else Modifier.border(2.dp, if (enabled) c.hairlineStrong else c.hairline, CircleShape),
                ),
                contentAlignment = Alignment.Center,
            ) {
                if (done) Icon(Glyphs.check, "Done", tint = c.onAccent, modifier = Modifier.size(16.dp))
                else Text("$number", style = Brand.label, color = if (enabled) c.text else c.textFaint)
            }
            Column(Modifier.weight(1f).padding(start = 14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Column {
                    Text(title, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, color = if (enabled) c.text else c.textFaint)
                    Text(body, style = MaterialTheme.typography.bodySmall, color = c.textDim, modifier = Modifier.padding(top = 2.dp))
                }
                actions()
            }
        }
    }
}

@Composable
private fun TestRow(account: AccountEntity, viewModel: InstantUpdatesViewModel) {
    val c = Brand.colors
    val state = viewModel.tests[account.id]
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text("${account.username} · ${account.server.toUri().host ?: account.server}", style = MaterialTheme.typography.bodyMedium, color = c.text)
        GhostButton(
            if (state is TestState.Arrived || state is TestState.Failed) "Send again" else "Send test push",
            onClick = { viewModel.test(account) },
            enabled = state !is TestState.Sending && state !is TestState.Waiting,
            icon = Glyphs.zap,
        )
        val (text, color) = when (state) {
            TestState.Sending -> "Asking the server…" to c.textDim
            is TestState.Waiting -> "Sent. Waiting for it to arrive…" to c.textDim
            is TestState.Arrived -> "Arrived after %.1f s. Instant updates work.".format(state.afterMs / 1000.0) to c.highlight
            is TestState.Failed -> state.message to c.dangerInk
            null -> viewModel.lastPush(account.id)?.let {
                "Last push: " + DateUtils.getRelativeTimeSpanString(it, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS) + "."
            }.orEmpty() to c.textFaint
        }
        if (text.isNotEmpty()) Text(text, style = MaterialTheme.typography.bodySmall, color = color)
    }
}

private const val NTFY = "io.heckel.ntfy"

/** The phone's app store (Play or F-Droid answer market:), else F-Droid's page. */
private fun openStore(context: Context) {
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, "market://details?id=$NTFY".toUri()))
    } catch (_: ActivityNotFoundException) {
        context.open("https://f-droid.org/packages/$NTFY/")
    }
}

private fun Context.open(url: String) = runCatching { startActivity(Intent(Intent.ACTION_VIEW, url.toUri())) }

private fun Context.launchApp(packageName: String) {
    packageManager.getLaunchIntentForPackage(packageName)?.let { runCatching { startActivity(it) } }
}
