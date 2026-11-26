package org.hyperstarit.twentyfourseven.sync

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.DeviceInfo
import org.hyperstarit.twentyfourseven.auth.LegacySession
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.auth.Tokens
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Two servers restored from one backup hand out the same ids: each account's rows, pending edits and
 * queue must stay its own through sync, edits and removal.
 */
/** A device registration that fails is tried again at the next sync — for that account only. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class DeviceRegistrationTest {
    private class MemorySecrets : SecretStore {
        val tokens = mutableMapOf<String, Tokens>()
        override suspend fun tokens(accountId: String) = tokens[accountId]
        override suspend fun saveTokens(accountId: String, tokens: Tokens?) {
            if (tokens == null) this.tokens.remove(accountId) else this.tokens[accountId] = tokens
        }
        override suspend fun deviceId() = "device-1"
        override suspend fun takeLegacySession(): LegacySession? = null
    }

    private val flaky = FakeServer()
    private val steady = FakeServer()
    private lateinit var db: AppDatabase

    @After
    fun tearDown() {
        db.close()
        flaky.server.close()
        steady.server.close()
    }

    @Test
    fun aFailedRegistration_IsRetried_WhileTheOtherAccountIsLeftAlone() = runBlocking {
        db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java)
            .allowMainThreadQueries().build()
        val device = object : DeviceInfo {
            override val name = "Test"
            override val appVersion = "0"
        }
        var endpoint: org.hyperstarit.twentyfourseven.net.DevicePushInput? = null
        val accounts = Accounts(db, MemorySecrets(), OkHttpClient(), Json { ignoreUnknownKeys = true }, Clock.systemUTC(), device) { endpoint }
        val changes = LocalChanges(db, Clock.systemUTC()) {}
        val runner = SyncRunner(db, accounts, CalendarItSync(db, accounts, Clock.systemUTC(), changes))

        flaky.failDeviceRegistrations = 2 // at sign-in and at the first sync: a temporary outage
        accounts.addCalendarIt(flaky.url(), "me@test", "pw", pin = null)
        accounts.addCalendarIt(steady.url(), "me@test", "pw", pin = null)
        assertEquals(0, flaky.deviceRegistrations.get())
        assertEquals(1, steady.deviceRegistrations.get())

        runner.syncAll()
        assertEquals(0, flaky.deviceRegistrations.get()) // still failing
        assertEquals(1, steady.deviceRegistrations.get()) // already known: not asked again

        runner.syncAll()
        assertEquals(1, flaky.deviceRegistrations.get()) // recovered: registered now
        assertEquals(1, steady.deviceRegistrations.get())

        runner.syncAll()
        assertEquals(1, flaky.deviceRegistrations.get()) // and not again after that
        assertEquals(1, steady.deviceRegistrations.get())

        // A new push endpoint (which reached no server while offline) goes to both at the next sync.
        endpoint = org.hyperstarit.twentyfourseven.net.DevicePushInput("unifiedpush", "https://push.example/up/1")
        runner.syncAll()
        assertEquals(2, flaky.deviceRegistrations.get())
        assertEquals(2, steady.deviceRegistrations.get())
    }
}
