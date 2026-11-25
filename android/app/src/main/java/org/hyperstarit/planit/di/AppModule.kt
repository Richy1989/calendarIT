package org.hyperstarit.planit.di

import android.content.Context
import android.os.Build
import android.provider.Settings
import androidx.datastore.preferences.preferencesDataStore
import androidx.room.Room
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import java.time.Clock
import java.util.concurrent.TimeUnit
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import org.hyperstarit.planit.BuildConfig
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.accounts.DeviceInfo
import org.hyperstarit.planit.auth.DataStoreSecretStore
import org.hyperstarit.planit.auth.KeystoreSecretBox
import org.hyperstarit.planit.auth.SecretStore
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.sync.LocalChanges
import org.hyperstarit.planit.sync.PlanItSync
import org.hyperstarit.planit.sync.SyncRunner
import org.hyperstarit.planit.sync.SyncScheduler

// The file keeps phase 1's name ("session"): its device id and old session are read from it.
private val Context.secretsDataStore by preferencesDataStore(name = "session")
private val Context.uiDataStore by preferencesDataStore(name = "ui")

@Module
@InstallIn(SingletonComponent::class)
object AppModule {

    @Provides
    @Singleton
    fun json(): Json = Json {
        ignoreUnknownKeys = true // the server may add fields; protocol 1 only ever adds
        explicitNulls = false
    }

    @Provides
    @Singleton
    fun clock(): Clock = Clock.systemUTC()

    @Provides
    @Singleton
    fun scope(): CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    /** The server lists sessions by User-Agent, so the user can tell this device's sign-in apart. */
    @Provides
    @Singleton
    fun okHttp(): OkHttpClient {
        val agent = "PlanIt/${BuildConfig.VERSION_NAME} (Android ${Build.VERSION.RELEASE}; ${Build.MODEL})"
        return OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(60, TimeUnit.SECONDS)
            .followRedirects(false) // a redirect could carry the password elsewhere
            .addInterceptor { chain -> chain.proceed(chain.request().newBuilder().header("User-Agent", agent).build()) }
            .build()
    }

    @Provides
    @Singleton
    fun secrets(@ApplicationContext context: Context, json: Json): SecretStore =
        DataStoreSecretStore(context.secretsDataStore, KeystoreSecretBox(), json)

    @Provides
    @Singleton
    fun database(@ApplicationContext context: Context): PlanItDatabase =
        Room.databaseBuilder(context, PlanItDatabase::class.java, "planit.db")
            .addMigrations(PlanItDatabase.MIGRATION_1_2)
            .build()

    @Provides
    @Singleton
    fun deviceInfo(@ApplicationContext context: Context): DeviceInfo = object : DeviceInfo {
        override val name: String =
            Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
                ?: "${Build.MANUFACTURER} ${Build.MODEL}"
        override val appVersion: String = BuildConfig.VERSION_NAME
    }

    @Provides
    @Singleton
    fun accounts(db: PlanItDatabase, secrets: SecretStore, http: OkHttpClient, json: Json, clock: Clock, device: DeviceInfo, push: org.hyperstarit.planit.push.PushPrefs) =
        Accounts(db, secrets, http, json, clock, device, pushFor = push::endpoint)

    @Provides
    @Singleton
    fun pushPrefs(@ApplicationContext context: Context) = org.hyperstarit.planit.push.PushPrefs(context)

    @Provides
    @Singleton
    fun pushRegistrar(@ApplicationContext context: Context, db: PlanItDatabase, accounts: Accounts, prefs: org.hyperstarit.planit.push.PushPrefs) =
        org.hyperstarit.planit.push.PushRegistrar(context, db, accounts, prefs)

    @Provides
    @Singleton
    fun calendarPrefs(@ApplicationContext context: Context) = org.hyperstarit.planit.calendar.CalendarPrefs(context.uiDataStore)

    @Provides
    @Singleton
    fun scheduler(@ApplicationContext context: Context) = SyncScheduler(context)

    @Provides
    @Singleton
    fun reminderPrefs(@ApplicationContext context: Context) = org.hyperstarit.planit.reminders.ReminderPrefs(context)

    @Provides
    @Singleton
    fun reminderScheduler(@ApplicationContext context: Context, db: PlanItDatabase, prefs: org.hyperstarit.planit.reminders.ReminderPrefs, clock: Clock) =
        org.hyperstarit.planit.reminders.ReminderScheduler(context, db, prefs, clock)

    /** A change is shown at once, queued for the server, and its reminders set at once (even offline). */
    @Provides
    @Singleton
    fun localChanges(
        @ApplicationContext context: Context, db: PlanItDatabase, clock: Clock, scheduler: SyncScheduler,
        reminders: org.hyperstarit.planit.reminders.ReminderScheduler, scope: CoroutineScope,
        // Lazy: the system calendar takes in other apps' changes through LocalChanges itself.
        system: dagger.Lazy<org.hyperstarit.planit.system.SystemCalendar>,
    ) = LocalChanges(db, clock, onQueued = {
        scheduler.requestNow()
        scope.launch {
            reminders.reschedule()
            org.hyperstarit.planit.widget.MonthWidget.refresh(context)
            system.get().sync()
        }
    })

    @Provides
    @Singleton
    fun systemCalendar(@ApplicationContext context: Context, db: PlanItDatabase, changes: LocalChanges) =
        org.hyperstarit.planit.system.SystemCalendar(context, db, changes)

    @Provides
    @Singleton
    fun planItSync(db: PlanItDatabase, accounts: Accounts, clock: Clock, changes: LocalChanges) =
        PlanItSync(db, accounts, clock, changes)

    @Provides
    @Singleton
    fun runner(
        @ApplicationContext context: Context, db: PlanItDatabase, accounts: Accounts, planIt: PlanItSync, reminders: org.hyperstarit.planit.reminders.ReminderScheduler,
        system: org.hyperstarit.planit.system.SystemCalendar,
    ) = SyncRunner(db, accounts, planIt, afterSync = {
        reminders.reschedule()
        system.sync()
        org.hyperstarit.planit.widget.MonthWidget.refresh(context)
    })
}
