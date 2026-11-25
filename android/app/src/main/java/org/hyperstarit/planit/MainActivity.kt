package org.hyperstarit.planit

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import org.hyperstarit.planit.auth.AuthState
import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.ui.PlanItRoot
import org.hyperstarit.planit.ui.theme.PlanItTheme

@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    @Inject lateinit var session: SessionManager

    override fun onCreate(savedInstanceState: Bundle?) {
        // The splash stays until the stored session is read, so the login screen never flashes.
        installSplashScreen().setKeepOnScreenCondition { session.state.value == AuthState.Loading }
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            PlanItTheme {
                PlanItRoot()
            }
        }
    }
}
