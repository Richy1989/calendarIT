package org.hyperstarit.twentyfourseven

import android.app.Application
import androidx.hilt.work.HiltWorkerFactory
import androidx.work.Configuration
import dagger.hilt.android.HiltAndroidApp
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.sync.SyncScheduler

@HiltAndroidApp
class TwentyFourSevenApp : Application(), Configuration.Provider {
    @Inject lateinit var workerFactory: HiltWorkerFactory
    @Inject lateinit var accounts: Accounts
    @Inject lateinit var scheduler: SyncScheduler
    @Inject lateinit var scope: CoroutineScope
    @Inject lateinit var reminders: org.hyperstarit.twentyfourseven.reminders.ReminderScheduler

    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder().setWorkerFactory(workerFactory).build()

    override fun onCreate() {
        super.onCreate()
        org.hyperstarit.twentyfourseven.reminders.ReminderReceiver.createChannel(this)
        scope.launch {
            reminders.reschedule()
            accounts.adoptLegacySession()
            scheduler.schedulePeriodic()
            scheduler.requestNow()
        }
    }
}
