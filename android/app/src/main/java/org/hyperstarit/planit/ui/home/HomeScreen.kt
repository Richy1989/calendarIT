package org.hyperstarit.planit.ui.home

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ExitToApp
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
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
import kotlinx.coroutines.launch
import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.data.CalendarRepository

@HiltViewModel
class HomeViewModel @Inject constructor(
    private val repository: CalendarRepository,
    private val session: SessionManager,
) : ViewModel() {
    val calendars = repository.calendars
    var refreshing by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    init {
        refresh()
    }

    fun refresh() {
        if (refreshing) return
        refreshing = true
        viewModelScope.launch {
            error = try {
                repository.refresh()
                null
            } catch (e: IOException) {
                e.message ?: "Couldn't reach the server."
            } finally {
                refreshing = false
            }
        }
    }

    fun signOut() {
        viewModelScope.launch { session.logout() }
    }
}

/** Phase 1's home: who is signed in where, and the calendars the server sent. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(server: String, email: String, viewModel: HomeViewModel = hiltViewModel()) {
    val calendars by viewModel.calendars.collectAsStateWithLifecycle(initialValue = emptyList())
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("PlanIt") },
                actions = {
                    IconButton(onClick = viewModel::refresh) { Icon(Icons.Default.Refresh, contentDescription = "Refresh") }
                    IconButton(onClick = viewModel::signOut) {
                        Icon(Icons.AutoMirrored.Filled.ExitToApp, contentDescription = "Sign out")
                    }
                },
            )
        },
    ) { padding ->
        LazyColumn(modifier = Modifier.fillMaxSize().padding(padding)) {
            item {
                if (viewModel.refreshing) LinearProgressIndicator(modifier = Modifier.padding(horizontal = 16.dp))
                ListItem(supportingContent = { Text(server) }) { Text(email) }
                viewModel.error?.let {
                    Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp))
                }
            }
            items(calendars, key = { it.id }) { calendar ->
                ListItem(
                    leadingContent = {
                        Box(
                            Modifier.size(14.dp).clip(CircleShape)
                                .background(calendar.color?.let(::parseColor) ?: MaterialTheme.colorScheme.primary),
                        )
                    },
                    supportingContent = if (calendar.readOnly) ({ Text("Subscription · read-only") }) else null,
                ) { Text(calendar.name) }
            }
        }
    }
}

private fun parseColor(hex: String): Color? = runCatching {
    Color(hex.toColorInt())
}.getOrNull()
