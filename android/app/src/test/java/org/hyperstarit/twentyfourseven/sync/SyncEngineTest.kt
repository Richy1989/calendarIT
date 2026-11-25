package org.hyperstarit.twentyfourseven.sync

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
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
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class SyncEngineTest {
    private class MemorySecrets : SecretStore {
        val tokens = mutableMapOf<String, Tokens>()
        override suspend fun tokens(accountId: String) = tokens[accountId]
        override suspend fun saveTokens(accountId: String, tokens: Tokens?) {
            if (tokens == null) this.tokens.remove(accountId) else this.tokens[accountId] = tokens
        }
        override suspend fun deviceId() = "device-1"
        override suspend fun takeLegacySession(): LegacySession? = null
    }

    private val fake = FakeServer()
    private lateinit var db: AppDatabase
    private lateinit var accounts: Accounts
    private lateinit var changes: LocalChanges
    private lateinit var runner: SyncRunner
    private lateinit var account: AccountEntity
    private var queued = 0

    @Before
    fun setUp() = runBlocking {
        db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java)
            .allowMainThreadQueries().build()
        val device = object : DeviceInfo {
            override val name = "Test"
            override val appVersion = "0"
        }
        accounts = Accounts(db, MemorySecrets(), OkHttpClient(), Json { ignoreUnknownKeys = true }, Clock.systemUTC(), device)
        changes = LocalChanges(db, Clock.systemUTC()) { queued++ }
        runner = SyncRunner(db, accounts, CalendarItSync(db, accounts, Clock.systemUTC(), changes))
        account = accounts.addCalendarIt(fake.url(), "me@test", "pw", pin = null)
    }

    @After
    fun tearDown() {
        db.close()
        fake.server.close()
    }

    private fun ops(push: Int = fake.pushes.size - 1) = fake.pushes[push]["ops"]!!.jsonArray.map { it.jsonObject }

    private suspend fun liveTitles() = db.events().observeLive().first().map { it.data.title }.sorted()

    @Test
    fun aFirstSync_StoresEverything_AndTheNextOnesApplyChanges() = runBlocking {
        val override = """{"id":"o1","recurrenceId":"2026-08-10T07:00:00+00:00","title":"Moved","start":"2026-08-10T09:00:00+00:00","allDay":false,"reminders":[]}"""
        fake.pages = mutableListOf(
            FakeServer.page(events = listOf(FakeServer.event("e1", "Standup", overrides = override, recurrence = "FREQ=WEEKLY")), hasMore = true, cursor = "p1"),
            FakeServer.page(events = listOf(FakeServer.event("e2", "Lunch")), tasks = listOf(FakeServer.task("t1", "Milk")), cursor = "c1"),
            FakeServer.page(deletedEvents = listOf("e2"), cursor = "c2"),
        )

        assertTrue(runner.syncAll())
        assertEquals(listOf("Lunch", "Standup"), liveTitles())
        assertEquals("Moved", db.events().observeLiveOverrides().first().single().data.title)
        assertEquals("Milk", db.tasks().observeLive().first().single().data.title)
        assertEquals("c1", db.accounts().get(account.id)!!.syncCursor)

        runner.syncAll()
        assertEquals(listOf(null, "p1", "c1"), fake.pullCursors.toList()) // pages follow on; the next sync starts at the cursor
        assertEquals(listOf("Standup"), liveTitles())
    }

    @Test
    fun aReset_ForgetsWhatTheFullListingDidntName() = runBlocking {
        fake.pages = mutableListOf(
            FakeServer.page(events = listOf(FakeServer.event("e1", "Old"), FakeServer.event("e2", "Kept"))),
            FakeServer.page(events = listOf(FakeServer.event("e2", "Kept")), reset = true, cursor = "c9"),
        )
        runner.syncAll()
        runner.syncAll()
        assertEquals(listOf("Kept"), liveTitles())
    }

    @Test
    fun anEdit_ShowsAtOnce_IsSentOnTheServersVersion_AndMakesWayForItsCopy() = runBlocking {
        fake.pages = mutableListOf(FakeServer.page(events = listOf(FakeServer.event("e1", "Standup", version = "10"))))
        runner.syncAll()
        val current = db.events().live("e1")!!.data

        changes.updateEvent(current.copy(title = "Standup (phone)"))
        assertEquals(listOf("Standup (phone)"), liveTitles()) // before any sync
        assertEquals(1, queued)

        fake.pages = mutableListOf(FakeServer.page(events = listOf(FakeServer.event("e1", "Standup (phone)", version = "11")), cursor = "c2"))
        runner.syncAll()

        val sent = ops().single()
        assertEquals("event.update", sent["op"]!!.jsonPrimitive.content)
        assertEquals("10", sent["baseVersion"]!!.jsonPrimitive.content)
        assertEquals("Standup (phone)", sent["data"]!!.jsonObject["title"]!!.jsonPrimitive.content)
        assertNull(db.events().edit("e1")) // settled: the server's copy shows
        assertEquals("11", db.events().live("e1")!!.data.version)
    }

    @Test
    fun queuedChanges_Fold_WhereTheResultIsTheSame() = runBlocking {
        runner.syncAll()
        val draft = db.events().live("none")
        assertNull(draft)
        val base = org.hyperstarit.twentyfourseven.data.db.EventData(
            id = "", accountId = account.id, calendarId = FakeServer.CAL, title = "Draft", description = null, location = null,
            categoryId = null, color = null, start = 0, end = 3_600_000, allDay = false, timeZone = "UTC", recurrence = null,
            exDates = emptyList(), reminders = emptyList(), attendees = emptyList(), invitationStatus = null, organizerEmail = null, version = null,
        )
        val id = changes.createEvent(base)
        changes.updateEvent(base.copy(id = id, title = "Draft, renamed"))
        val queue = db.outbox().forAccount(account.id)
        assertEquals(1, queue.size) // created as edited
        assertTrue(queue.single().data!!.contains("Draft, renamed"))

        val gone = changes.createEvent(base.copy(title = "Never mind"))
        changes.deleteEvent(gone)
        assertEquals(1, db.outbox().forAccount(account.id).size) // nothing to tell the server
        assertNull(db.events().edit(gone))
    }

    @Test
    fun aConflict_KeepsTheServersVersion_AndOffersToApplyMineAgain() = runBlocking {
        fake.pages = mutableListOf(FakeServer.page(events = listOf(FakeServer.event("e1", "Standup", version = "10"))))
        runner.syncAll()
        changes.updateEvent(db.events().live("e1")!!.data.copy(title = "Mine"))
        fake.pushAnswer = { op -> """{"opId":"${op["opId"]!!.jsonPrimitive.content}","status":"conflict"}""" }
        fake.pages = mutableListOf(FakeServer.page(events = listOf(FakeServer.event("e1", "Theirs", version = "12")), cursor = "c2"))

        runner.syncAll()

        assertEquals(listOf("Theirs"), liveTitles())
        val notice = db.notices().observeAll().first().single()
        assertEquals("conflict", notice.kind)
        assertEquals("event.update", notice.op)
        assertTrue(db.outbox().forAccount(account.id).isEmpty())

        changes.reapply(notice.id)
        val again = db.outbox().forAccount(account.id).single()
        assertEquals("12", again.baseVersion) // on the server's version now
        assertTrue(again.data!!.contains("Mine"))
    }

    @Test
    fun theVersionsInAnAnswer_BecomeTheBaseOfWhatIsStillQueued() = runBlocking {
        fake.pages = mutableListOf(FakeServer.page(tasks = listOf(FakeServer.task("t1", "Milk", version = "20"))))
        runner.syncAll()
        changes.setTaskDone("t1", true)
        changes.updateTask(db.tasks().live("t1")!!.data.copy(title = "Oat milk"))
        // The first push answers only the first change (and the task's new version); the second
        // stays queued and must then go out on that version.
        var calls = 0
        fake.pushAnswer = { op ->
            val id = op["opId"]!!.jsonPrimitive.content
            when {
                op["op"]!!.jsonPrimitive.content == "task.done" -> """{"opId":"$id","status":"ok","versions":{"t1":"21"}}"""
                calls++ == 0 -> """{"opId":"unrelated","status":"ok"}"""
                else -> """{"opId":"$id","status":"ok"}"""
            }
        }
        runner.syncAll()
        assertEquals("20", ops(fake.pushes.size - 2).last()["baseVersion"]!!.jsonPrimitive.content)
        val resent = ops(fake.pushes.size - 1).single()
        assertEquals("task.update", resent["op"]!!.jsonPrimitive.content)
        assertEquals("21", resent["baseVersion"]!!.jsonPrimitive.content)
    }

    @Test
    fun aServerError_KeepsTheChange_ForALaterTry() = runBlocking {
        runner.syncAll()
        changes.createTask(
            org.hyperstarit.twentyfourseven.data.db.TaskData(
                id = "", accountId = account.id, calendarId = FakeServer.CAL, title = "Later", notes = null, due = null,
                dueAllDay = false, timeZone = null, status = "NeedsAction", completedAt = null, priority = null, parentId = null,
                sortOrder = 0, reminders = emptyList(), version = null,
            ),
        )
        fake.pushAnswer = { op -> """{"opId":"${op["opId"]!!.jsonPrimitive.content}","status":"error","error":"db down"}""" }
        runner.syncAll()
        val kept = db.outbox().forAccount(account.id).single()
        assertEquals(1, kept.attempts)
        assertTrue(kept.nextAttemptAt > System.currentTimeMillis())
        assertEquals(listOf("Later"), db.tasks().observeLive().first().map { it.data.title }) // still shown
    }

    @Test
    fun anEndedSession_StopsSyncingThatAccount_UntilTheUserSignsInAgain() = runBlocking {
        fake.accessValidFor = 5 // the next call refreshes
        fake.refreshRefused = true
        account = accounts.addCalendarIt(fake.url(), "other@test", "pw", pin = null)
        runner.syncAll()
        assertTrue(db.accounts().get(account.id)!!.needsSignIn)

        val refreshes = fake.refreshes.get()
        runner.syncAll()
        assertEquals(refreshes, fake.refreshes.get()) // left alone: no knocking with a dead token

        fake.refreshRefused = false
        fake.accessValidFor = 900
        accounts.signInAgain(account.id, "pw")
        assertFalse(db.accounts().get(account.id)!!.needsSignIn)
    }

    @Test
    fun removingAnAccount_TakesAllOfItsDataAlong() = runBlocking {
        fake.pages = mutableListOf(FakeServer.page(events = listOf(FakeServer.event("e1", "Standup"))))
        runner.syncAll()
        accounts.remove(account.id)
        assertTrue(db.events().observeLive().first().isEmpty())
        assertTrue(db.calendars().observeAll().first().isEmpty())
        assertNull(db.accounts().get(account.id))
    }
}
