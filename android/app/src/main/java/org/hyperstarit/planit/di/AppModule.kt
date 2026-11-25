package org.hyperstarit.planit.di

import android.content.Context
import android.os.Build
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
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import org.hyperstarit.planit.BuildConfig
import org.hyperstarit.planit.auth.DataStoreSessionStore
import org.hyperstarit.planit.auth.KeystoreSecretBox
import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.auth.SessionStore
import org.hyperstarit.planit.data.CalendarRepository
import org.hyperstarit.planit.data.DeviceRegistrar
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.net.ApiClient

private val Context.sessionDataStore by preferencesDataStore(name = "session")

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
    fun api(http: OkHttpClient, json: Json): ApiClient = ApiClient(http, json)

    @Provides
    @Singleton
    fun sessionStore(@ApplicationContext context: Context, json: Json): SessionStore =
        DataStoreSessionStore(context.sessionDataStore, KeystoreSecretBox(), json)

    @Provides
    @Singleton
    fun database(@ApplicationContext context: Context): PlanItDatabase =
        Room.databaseBuilder(context, PlanItDatabase::class.java, "planit.db").build()

    @Provides
    @Singleton
    fun deviceRegistrar(@ApplicationContext context: Context, api: ApiClient, store: SessionStore) =
        DeviceRegistrar(context, api, store)

    @Provides
    @Singleton
    fun sessionManager(
        api: ApiClient,
        store: SessionStore,
        clock: Clock,
        scope: CoroutineScope,
        registrar: DeviceRegistrar,
        db: PlanItDatabase,
    ): SessionManager = SessionManager(
        api, store, clock, scope,
        onSignedIn = { registrar.register(it) },
        // Nothing of the account stays on the device after it signs out.
        onSignedOut = { withContext(Dispatchers.IO) { db.clearAllTables() } },
    )

    @Provides
    @Singleton
    fun calendarRepository(db: PlanItDatabase, api: ApiClient, session: SessionManager) =
        CalendarRepository(db, api, session)
}
