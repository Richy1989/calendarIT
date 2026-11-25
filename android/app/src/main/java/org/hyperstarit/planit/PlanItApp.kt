package org.hyperstarit.planit

import android.app.Application
import androidx.hilt.work.HiltWorkerFactory
import androidx.work.Configuration
import dagger.hilt.android.HiltAndroidApp
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.sync.SyncScheduler

@HiltAndroidApp
class PlanItApp : Application(), Configuration.Provider {
    @Inject lateinit var workerFactory: HiltWorkerFactory
    @Inject lateinit var accounts: Accounts
    @Inject lateinit var scheduler: SyncScheduler
    @Inject lateinit var scope: CoroutineScope

    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder().setWorkerFactory(workerFactory).build()

    override fun onCreate() {
        super.onCreate()
        scope.launch {
            accounts.adoptLegacySession()
            scheduler.schedulePeriodic()
            scheduler.requestNow()
        }
    }
}
