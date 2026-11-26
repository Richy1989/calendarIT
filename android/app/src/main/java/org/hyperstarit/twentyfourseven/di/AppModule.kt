package org.hyperstarit.twentyfourseven.di

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
import org.hyperstarit.twentyfourseven.BuildConfig
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.DeviceInfo
import org.hyperstarit.twentyfourseven.auth.DataStoreSecretStore
import org.hyperstarit.twentyfourseven.auth.KeystoreSecretBox
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.sync.LocalChanges
import org.hyperstarit.twentyfourseven.sync.CalendarItSync
import org.hyperstarit.twentyfourseven.sync.SyncRunner
import org.hyperstarit.twentyfourseven.sync.SyncScheduler

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
        val agent = "24seven/${BuildConfig.VERSION_NAME} (Android ${Build.VERSION.RELEASE}; ${Build.MODEL})"
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
    fun database(@ApplicationContext context: Context): AppDatabase =
        Room.databaseBuilder(context, AppDatabase::class.java, "app.db")
            .addMigrations(AppDatabase.MIGRATION_1_2, AppDatabase.MIGRATION_2_3, AppDatabase.MIGRATION_3_4)
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
    fun accounts(db: AppDatabase, secrets: SecretStore, http: OkHttpClient, json: Json, clock: Clock, device: DeviceInfo, push: org.hyperstarit.twentyfourseven.push.PushPrefs) =
        Accounts(db, secrets, http, json, clock, device, pushFor = push::endpoint)

    @Provides
    @Singleton
    fun pushPrefs(@ApplicationContext context: Context) = org.hyperstarit.twentyfourseven.push.PushPrefs(context)

    @Provides
    @Singleton
    fun pushRegistrar(@ApplicationContext context: Context, db: AppDatabase, accounts: Accounts, prefs: org.hyperstarit.twentyfourseven.push.PushPrefs) =
        org.hyperstarit.twentyfourseven.push.PushRegistrar(context, db, accounts, prefs)

    @Provides
    @Singleton
    fun calendarPrefs(@ApplicationContext context: Context) = org.hyperstarit.twentyfourseven.calendar.CalendarPrefs(context.uiDataStore)

    @Provides
    @Singleton
    fun scheduler(@ApplicationContext context: Context) = SyncScheduler(context)

    @Provides
    @Singleton
    fun reminderPrefs(@ApplicationContext context: Context) = org.hyperstarit.twentyfourseven.reminders.ReminderPrefs(context)

    @Provides
    @Singleton
    fun reminderScheduler(@ApplicationContext context: Context, db: AppDatabase, prefs: org.hyperstarit.twentyfourseven.reminders.ReminderPrefs, clock: Clock) =
        org.hyperstarit.twentyfourseven.reminders.ReminderScheduler(context, db, prefs, clock)

    /** A change is shown at once, queued for the server, and its reminders set at once (even offline). */
    @Provides
    @Singleton
    fun localChanges(
        @ApplicationContext context: Context, db: AppDatabase, clock: Clock, scheduler: SyncScheduler,
        reminders: org.hyperstarit.twentyfourseven.reminders.ReminderScheduler, scope: CoroutineScope,
        // Lazy: the system calendar takes in other apps' changes through LocalChanges itself.
        system: dagger.Lazy<org.hyperstarit.twentyfourseven.system.SystemCalendar>,
    ) = LocalChanges(db, clock, onQueued = {
        scheduler.requestNow()
        scope.launch {
            reminders.reschedule()
            org.hyperstarit.twentyfourseven.widget.MonthWidget.refresh(context)
            system.get().sync()
        }
    })

    @Provides
    @Singleton
    fun systemCalendar(@ApplicationContext context: Context, db: AppDatabase, changes: LocalChanges) =
        org.hyperstarit.twentyfourseven.system.SystemCalendar(context, db, changes)

    @Provides
    @Singleton
    fun calendarItSync(db: AppDatabase, accounts: Accounts, clock: Clock, changes: LocalChanges) =
        CalendarItSync(db, accounts, clock, changes)

    @Provides
    @Singleton
    fun runner(
        @ApplicationContext context: Context, db: AppDatabase, accounts: Accounts, calendarIt: CalendarItSync, reminders: org.hyperstarit.twentyfourseven.reminders.ReminderScheduler,
        system: org.hyperstarit.twentyfourseven.system.SystemCalendar,
    ) = SyncRunner(db, accounts, calendarIt, afterSync = {
        reminders.reschedule()
        system.sync()
        org.hyperstarit.twentyfourseven.widget.MonthWidget.refresh(context)
    })
}
