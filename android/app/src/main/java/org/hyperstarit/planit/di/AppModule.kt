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
    fun accounts(db: PlanItDatabase, secrets: SecretStore, http: OkHttpClient, json: Json, clock: Clock, device: DeviceInfo) =
        Accounts(db, secrets, http, json, clock, device)

    @Provides
    @Singleton
    fun calendarPrefs(@ApplicationContext context: Context) = org.hyperstarit.planit.calendar.CalendarPrefs(context.uiDataStore)

    @Provides
    @Singleton
    fun scheduler(@ApplicationContext context: Context) = SyncScheduler(context)

    @Provides
    @Singleton
    fun localChanges(db: PlanItDatabase, clock: Clock, scheduler: SyncScheduler) =
        LocalChanges(db, clock, onQueued = scheduler::requestNow)

    @Provides
    @Singleton
    fun planItSync(db: PlanItDatabase, accounts: Accounts, clock: Clock, changes: LocalChanges) =
        PlanItSync(db, accounts, clock, changes)

    @Provides
    @Singleton
    fun runner(db: PlanItDatabase, accounts: Accounts, planIt: PlanItSync) = SyncRunner(db, accounts, planIt)
}
