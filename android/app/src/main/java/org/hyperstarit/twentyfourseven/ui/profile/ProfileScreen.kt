package org.hyperstarit.twentyfourseven.ui.profile

import android.text.format.DateUtils
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedSecureTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import java.time.OffsetDateTime
import org.hyperstarit.twentyfourseven.net.SessionDto
import org.hyperstarit.twentyfourseven.ui.theme.Banner
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.GhostButton
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.IconTile
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.SettingRow
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import org.hyperstarit.twentyfourseven.ui.theme.TopBar
import org.hyperstarit.twentyfourseven.ui.theme.fieldColors

/** A 24seven account's profile: picture, address, password, and where it is signed in. */
@Composable
fun ProfileScreen(accountId: String, onBack: () -> Unit, viewModel: ProfileViewModel = hiltViewModel()) {
    BackHandler(onBack = onBack)
    LaunchedEffect(accountId) { viewModel.open(accountId) }
    val c = Brand.colors
    val profile = viewModel.profile
    var changingPassword by remember { mutableStateOf(false) }
    var confirmOthers by remember { mutableStateOf(false) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) viewModel.uploadAvatar(uri)
    }

    Scaffold(
        containerColor = Color.Transparent,
        topBar = {
            Column {
                TopBar(
                    "Profile",
                    subtitle = viewModel.account?.server,
                    navigation = { RoundIconButton(Glyphs.back, "Back", onBack) },
                    actions = { RoundIconButton(Glyphs.sync, "Reload", viewModel::reload) },
                )
                if (viewModel.loading || viewModel.busy) {
                    LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp), color = c.highlight, trackColor = Color.Transparent)
                }
            }
        },
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            viewModel.error?.let { Banner(it, action = "Retry", onClick = viewModel::reload) }
            viewModel.message?.let { Banner(it, warning = false) }

            Panel {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Avatar(viewModel.avatar, profile?.email ?: viewModel.account?.username.orEmpty())
                    Column(Modifier.weight(1f).padding(start = 14.dp)) {
                        Text(
                            profile?.email ?: viewModel.account?.username.orEmpty(),
                            style = MaterialTheme.typography.titleMedium, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis,
                        )
                        if (profile?.isAdmin == true) Text("Administrator", style = MaterialTheme.typography.bodySmall, color = c.highlight)
                    }
                }
                Row(Modifier.fillMaxWidth().padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    GhostButton(
                        if (viewModel.avatar == null) "Add picture" else "Change picture",
                        { pick.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                        Modifier.weight(1f), icon = Glyphs.edit, enabled = profile != null && !viewModel.busy,
                    )
                    if (viewModel.avatar != null) {
                        GhostButton("Remove", viewModel::removeAvatar, Modifier.weight(1f), icon = Glyphs.trash, danger = true, enabled = !viewModel.busy)
                    }
                }
            }

            SectionLabel("Security", Modifier.padding(start = 4.dp, top = 14.dp))
            Panel(padding = 0.dp) {
                SettingRow(
                    Glyphs.shield, "Change password",
                    subtitle = "Signs out your other devices and browsers.",
                    onClick = { if (profile != null) changingPassword = true },
                    trailing = { Icon(Glyphs.chevronRight, null, tint = c.textFaint, modifier = Modifier.size(20.dp)) },
                )
            }

            Row(Modifier.fillMaxWidth().padding(start = 4.dp, top = 14.dp), verticalAlignment = Alignment.CenterVertically) {
                SectionLabel("Signed in", Modifier.weight(1f))
                if (viewModel.sessions.size > 1) TextAction("Sign out all others", { confirmOthers = true }, color = c.dangerInk, enabled = !viewModel.busy)
            }
            if (viewModel.sessions.isNotEmpty()) {
                Panel(padding = 0.dp) {
                    viewModel.sessions.forEachIndexed { i, session ->
                        if (i > 0) Hairline(Modifier.padding(start = 66.dp))
                        SessionRow(session, enabled = !viewModel.busy, onSignOut = { viewModel.signOut(session) })
                    }
                }
            }
        }
    }

    if (changingPassword) {
        ChangePasswordDialog(viewModel, onDone = { changingPassword = false })
    }
    if (confirmOthers) {
        AlertDialog(
            onDismissRequest = { confirmOthers = false },
            title = { Text("Sign out everywhere else?") },
            text = { Text("Every other device and browser has to sign in again. This phone stays signed in.", color = c.textDim) },
            confirmButton = { TextAction("Sign out", { viewModel.signOutOthers(); confirmOthers = false }, color = c.dangerInk) },
            dismissButton = { TextAction("Cancel", { confirmOthers = false }, color = c.textDim) },
        )
    }
}

@Composable
private fun Avatar(image: androidx.compose.ui.graphics.ImageBitmap?, name: String) {
    val c = Brand.colors
    val shape = CircleShape
    Box(Modifier.size(64.dp).clip(shape).background(c.gradSoft).border(1.dp, c.hairline, shape), contentAlignment = Alignment.Center) {
        if (image != null) {
            Image(image, "Profile picture", Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        } else {
            Text(name.firstOrNull()?.uppercase() ?: "?", style = MaterialTheme.typography.headlineSmall, color = c.link)
        }
    }
}

@Composable
private fun SessionRow(session: SessionDto, enabled: Boolean, onSignOut: () -> Unit) {
    val c = Brand.colors
    val described = describeAgent(session.userAgent)
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        IconTile(if (described.phone) Glyphs.phone else Glyphs.globe)
        Column(Modifier.weight(1f).padding(horizontal = 14.dp)) {
            Text(described.name, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val details = listOfNotNull(
                if (session.current) "This phone" else "Active ${relative(session.lastActiveAt)}",
                session.ipAddress,
            ).joinToString(" · ")
            Text(details, style = MaterialTheme.typography.bodySmall, color = if (session.current) c.highlight else c.textDim)
        }
        if (!session.current) TextAction("Sign out", onSignOut, color = c.dangerInk, enabled = enabled)
    }
}

@Composable
private fun ChangePasswordDialog(viewModel: ProfileViewModel, onDone: () -> Unit) {
    val current = remember { TextFieldState() }
    val new = remember { TextFieldState() }
    val repeat = remember { TextFieldState() }
    var error by remember { mutableStateOf<String?>(null) }
    AlertDialog(
        onDismissRequest = { if (!viewModel.busy) onDone() },
        title = { Text("Change password") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedSecureTextField(state = current, label = { Text("Current password") }, shape = RoundedCornerShape(12.dp), colors = fieldColors())
                OutlinedSecureTextField(state = new, label = { Text("New password") }, shape = RoundedCornerShape(12.dp), colors = fieldColors())
                OutlinedSecureTextField(state = repeat, label = { Text("Repeat new password") }, shape = RoundedCornerShape(12.dp), colors = fieldColors())
                Text("Other devices and browsers are signed out; this phone stays signed in.", style = MaterialTheme.typography.bodySmall, color = Brand.colors.textDim)
                error?.let { Text(it, color = Brand.colors.dangerInk) }
            }
        },
        confirmButton = {
            TextAction("Change", enabled = !viewModel.busy, onClick = {
                val n = new.text.toString()
                error = when {
                    current.text.isEmpty() -> "Enter your current password."
                    n.length < 8 -> "The new password needs at least 8 characters."
                    n != repeat.text.toString() -> "The new passwords don't match."
                    else -> null
                }
                if (error == null) {
                    viewModel.changePassword(current.text.toString(), n) { failure ->
                        if (failure == null) onDone() else error = failure
                    }
                }
            })
        },
        dismissButton = { TextAction("Cancel", onDone, color = Brand.colors.textDim, enabled = !viewModel.busy) },
    )
}

/** A readable name for a session's User-Agent: "24seven on Pixel 8", "Firefox on Windows". */
internal data class AgentName(val name: String, val phone: Boolean)

internal fun describeAgent(agent: String?): AgentName {
    if (agent.isNullOrBlank()) return AgentName("Unknown device", phone = false)
    Regex("""^24seven/\S+ \(Android [^;]*; ([^)]+)\)""").find(agent)?.let {
        return AgentName("24seven on ${it.groupValues[1].trim()}", phone = true)
    }
    val browser = when {
        "Edg/" in agent -> "Edge"
        "OPR/" in agent -> "Opera"
        "Firefox/" in agent -> "Firefox"
        "Chrome/" in agent || "CriOS/" in agent -> "Chrome"
        "Safari/" in agent -> "Safari"
        else -> null
    }
    val os = when {
        "Android" in agent -> "Android"
        "iPhone" in agent || "iPad" in agent -> "iOS"
        "Windows" in agent -> "Windows"
        "Mac OS X" in agent || "Macintosh" in agent -> "macOS"
        "Linux" in agent -> "Linux"
        else -> null
    }
    val phone = os == "Android" || os == "iOS"
    return when {
        browser != null && os != null -> AgentName("$browser on $os", phone)
        browser != null -> AgentName(browser, phone)
        else -> AgentName(agent.take(60), phone)
    }
}

private fun relative(iso: String): String = runCatching {
    DateUtils.getRelativeTimeSpanString(OffsetDateTime.parse(iso).toInstant().toEpochMilli()).toString()
}.getOrDefault(iso)
