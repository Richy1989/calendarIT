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
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.auth.Tokens
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Two servers restored from one backup hand out the same ids: each account's rows, pending edits and
 * queue must stay its own through sync, edits and removal.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class AccountIsolationTest {
    private class MemorySecrets : SecretStore {
        val tokens = mutableMapOf<String, Tokens>()
        override suspend fun tokens(accountId: String) = tokens[accountId]
        override suspend fun saveTokens(accountId: String, tokens: Tokens?) {
            if (tokens == null) this.tokens.remove(accountId) else this.tokens[accountId] = tokens
        }
        override suspend fun deviceId() = "device-1"
    }

    private val serverA = FakeServer()
    private val serverB = FakeServer()
    private lateinit var db: AppDatabase
    private lateinit var accounts: Accounts
    private lateinit var changes: LocalChanges
    private lateinit var runner: SyncRunner
    private lateinit var a: AccountEntity
    private lateinit var b: AccountEntity

    private fun override(title: String) =
        """{"id":"o1","recurrenceId":"2026-08-10T07:00:00+00:00","title":"$title","start":"2026-08-10T09:00:00+00:00","allDay":false,"reminders":[]}"""

    private fun page(who: String, cursor: String = "c1") = FakeServer.page(
        events = listOf(FakeServer.event("e1", "$who standup", overrides = override("$who moved"), recurrence = "FREQ=WEEKLY")),
        tasks = listOf(FakeServer.task("t1", "$who milk")),
        cursor = cursor,
    )

    @Before
    fun setUp() = runBlocking {
        db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java)
            .allowMainThreadQueries().build()
        val device = object : DeviceInfo {
            override val name = "Test"
            override val appVersion = "0"
        }
        accounts = Accounts(db, MemorySecrets(), OkHttpClient(), Json { ignoreUnknownKeys = true }, Clock.systemUTC(), device)
        changes = LocalChanges(db, Clock.systemUTC()) {}
        runner = SyncRunner(db, accounts, CalendarItSync(db, accounts, Clock.systemUTC(), changes))
        a = accounts.addCalendarIt(serverA.url(), "me@test", "pw", pin = null)
        b = accounts.addCalendarIt(serverB.url(), "me@test", "pw", pin = null)
        serverA.pages = mutableListOf(page("A"))
        serverB.pages = mutableListOf(page("B"))
    }

    @After
    fun tearDown() {
        db.close()
        serverA.server.close()
        serverB.server.close()
    }

    private suspend fun titles() = db.events().observeLive().first().map { it.data.accountId to it.data.title }.toSet()
    private suspend fun overrideTitles() = db.events().observeLiveOverrides().first().map { it.data.accountId to it.data.title }.toSet()
    private suspend fun taskTitles() = db.tasks().observeLive().first().map { it.data.accountId to it.data.title }.toSet()

    @Test
    fun identicalIds_StayApart_ThroughSyncEditsAndRemoval() = runBlocking {
        runner.syncAll()
        assertEquals(setOf(a.id to "A standup", b.id to "B standup"), titles())
        assertEquals(setOf(a.id to "A moved", b.id to "B moved"), overrideTitles())
        assertEquals(setOf(a.id to "A milk", b.id to "B milk"), taskTitles())
        assertEquals(2, db.calendars().observeAll().first().size)

        // Offline edits on A, while B's server changes the same ids.
        serverA.server.close()
        changes.updateEvent(db.events().live(a.id, "e1")!!.data.copy(title = "A standup (phone)"))
        changes.setTaskDone(a.id, "t1", true)
        serverB.pages = mutableListOf(
            FakeServer.page(
                events = listOf(FakeServer.event("e1", "B renamed", version = "11", overrides = override("B moved"), recurrence = "FREQ=WEEKLY")),
                tasks = listOf(FakeServer.task("t1", "B milk", version = "21")),
                cursor = "c2",
            ),
        )
        runner.syncAll() // A can't be reached; B pulls
        assertEquals(setOf(a.id to "A standup (phone)", b.id to "B renamed"), titles())
        assertEquals(setOf("Completed" to a.id, "NeedsAction" to b.id), db.tasks().observeLive().first().map { it.data.status to it.data.accountId }.toSet())
        assertEquals(2, db.outbox().forAccount(a.id).size)
        assertTrue(db.outbox().forAccount(b.id).isEmpty())
        assertEquals("10", db.outbox().forEntity(a.id, "e1").single().baseVersion) // A's version, not B's 11

        // B edits the same id: its queue and edit don't touch A's.
        changes.updateEvent(db.events().live(b.id, "e1")!!.data.copy(title = "B (phone)"))
        assertEquals("11", db.outbox().forEntity(b.id, "e1").single().baseVersion)
        assertEquals("10", db.outbox().forEntity(a.id, "e1").single().baseVersion)

        // Removing B leaves every row of A's, its pending edits and queue included.
        accounts.remove(b.id)
        assertEquals(setOf(a.id to "A standup (phone)"), titles())
        assertEquals(setOf(a.id to "A moved"), overrideTitles())
        assertEquals(setOf(a.id to "A milk"), taskTitles())
        assertNotNull(db.events().server(a.id, "e1"))
        assertNotNull(db.events().edit(a.id, "e1"))
        assertEquals(1, db.calendars().observeAll().first().size)
        assertEquals(2, db.outbox().forAccount(a.id).size)
        assertNull(db.events().server(b.id, "e1"))
    }

    @Test
    fun aPushAnswer_RebasesOnlyItsOwnAccountsQueue() = runBlocking {
        runner.syncAll()
        // B has a change waiting (its server is down); A's push is answered with a new version for e1.
        serverB.server.close()
        changes.updateEvent(db.events().live(b.id, "e1")!!.data.copy(title = "B (phone)"))
        changes.updateEvent(db.events().live(a.id, "e1")!!.data.copy(title = "A (phone)"))
        serverA.pushAnswer = { op ->
            """{"opId":"${op["opId"]!!.jsonPrimitive.content}","status":"ok","versions":{"e1":"99"}}"""
        }
        serverA.pages = mutableListOf(
            FakeServer.page(events = listOf(FakeServer.event("e1", "A (phone)", version = "99", overrides = override("A moved"), recurrence = "FREQ=WEEKLY")), cursor = "c2"),
        )
        runner.syncAll()
        assertEquals("e1", serverA.pushes.single()["ops"]!!.jsonArray.single().jsonObject["id"]!!.jsonPrimitive.content)
        assertEquals("10", db.outbox().forEntity(b.id, "e1").single().baseVersion) // untouched by A's answer
        assertNull(db.events().edit(a.id, "e1")) // A's settled
        assertNotNull(db.events().edit(b.id, "e1")) // B's still waiting
        assertEquals(setOf(a.id to "A (phone)", b.id to "B (phone)"), titles())
    }
}
