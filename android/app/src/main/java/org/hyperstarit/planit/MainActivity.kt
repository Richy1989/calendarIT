package org.hyperstarit.planit

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.launch
import org.hyperstarit.planit.sync.SyncScheduler
import org.hyperstarit.planit.ui.PlanItRoot
import org.hyperstarit.planit.ui.RootViewModel
import org.hyperstarit.planit.ui.theme.PlanItTheme

@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    companion object {
        const val EXTRA_DATE = "org.hyperstarit.planit.DATE"
    }

    @Inject lateinit var scheduler: SyncScheduler
    @Inject lateinit var push: org.hyperstarit.planit.push.PushRegistrar
    @Inject lateinit var scope: kotlinx.coroutines.CoroutineScope
    private val root: RootViewModel by viewModels()

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        openFrom(intent)
    }

    /** A day tapped in the widget opens that day. */
    private fun openFrom(intent: android.content.Intent?) {
        intent?.getStringExtra(EXTRA_DATE)?.let { runCatching { java.time.LocalDate.parse(it) }.getOrNull() }?.let { root.openDate.value = it }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        // The splash stays until the accounts are read, so the wrong screen never flashes.
        installSplashScreen().setKeepOnScreenCondition { root.hasAccounts.value == null }
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // Coming back to the app is a good moment to catch up.
        lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) = scheduler.requestNow()
        })
        // Instant updates when a UnifiedPush distributor is set (or the only one installed); this
        // also renews the endpoints at every start.
        org.unifiedpush.android.connector.UnifiedPush.tryUseCurrentOrDefaultDistributor(this) { ok ->
            if (ok) scope.launch { push.registerAll() }
        }
        openFrom(intent)
        setContent {
            PlanItTheme { PlanItRoot(root) }
        }
    }
}
