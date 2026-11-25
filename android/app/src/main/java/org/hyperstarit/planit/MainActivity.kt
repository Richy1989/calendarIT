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
import org.hyperstarit.planit.sync.SyncScheduler
import org.hyperstarit.planit.ui.PlanItRoot
import org.hyperstarit.planit.ui.RootViewModel
import org.hyperstarit.planit.ui.theme.PlanItTheme

@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    @Inject lateinit var scheduler: SyncScheduler
    private val root: RootViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        // The splash stays until the accounts are read, so the wrong screen never flashes.
        installSplashScreen().setKeepOnScreenCondition { root.hasAccounts.value == null }
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // Coming back to the app is a good moment to catch up.
        lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) = scheduler.requestNow()
        })
        setContent {
            PlanItTheme { PlanItRoot(root) }
        }
    }
}
