package org.hyperstarit.twentyfourseven.accounts

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.auth.Tokens
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.net.ApiException
import org.hyperstarit.twentyfourseven.sync.FakeServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertThrows
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** Password changes (the phone signs in again with the new one) and reset links. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class ChangePasswordTest {
    private class MemorySecrets : SecretStore {
        val tokens = mutableMapOf<String, Tokens>()
        override suspend fun tokens(accountId: String) = tokens[accountId]
        override suspend fun saveTokens(accountId: String, tokens: Tokens?) {
            if (tokens == null) this.tokens.remove(accountId) else this.tokens[accountId] = tokens
        }
        override suspend fun deviceId() = "device-1"
    }

    private val fake = FakeServer()
    private val secrets = MemorySecrets()
    private lateinit var db: AppDatabase
    private lateinit var accounts: Accounts

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java)
            .allowMainThreadQueries().build()
        val device = object : DeviceInfo {
            override val name = "Test"
            override val appVersion = "0"
        }
        accounts = Accounts(db, secrets, OkHttpClient(), Json { ignoreUnknownKeys = true }, Clock.systemUTC(), device)
    }

    @After
    fun tearDown() {
        db.close()
        fake.server.close()
    }

    @Test
    fun afterAChange_TheAccountKeepsWorking_OnANewSession() = runBlocking {
        fake.accessValidFor = 10 // so the next call refreshes: the old session would be refused
        val account = accounts.addCalendarIt(fake.url(), "me@test", "pw", pin = null)

        accounts.changePassword(account, "pw", "new-password")

        assertEquals(1, fake.passwordChanges.get())
        assertEquals(2, fake.logins.get()) // signed in again, with the new password
        assertNotNull(secrets.tokens[account.id])
        assertFalse(db.accounts().get(account.id)!!.needsSignIn)
        // The account's session works on: a refresh (with the new session's token) is accepted.
        accounts.auth(db.accounts().get(account.id)!!).authorized { _, token -> assertEquals("access", token) }
        assertFalse(db.accounts().get(account.id)!!.needsSignIn)
    }

    @Test
    fun aWrongCurrentPassword_ChangesNothing() = runBlocking {
        val account = accounts.addCalendarIt(fake.url(), "me@test", "pw", pin = null)

        val e = assertThrows(ApiException::class.java) { runBlocking { accounts.changePassword(account, "nope", "new-password") } }

        assertEquals("Incorrect password.", e.message)
        assertEquals(0, fake.passwordChanges.get())
        assertEquals(1, fake.logins.get())
        assertFalse(db.accounts().get(account.id)!!.needsSignIn)
    }

    @Test
    fun aForgottenPassword_AsksTheServerForAResetLink() = runBlocking {
        accounts.requestPasswordReset(fake.url(), "  me@test ", pin = null)

        assertEquals(listOf("me@test"), fake.resetRequests)
        assertEquals(0, fake.logins.get()) // no account is added, nothing signs in
    }
}
