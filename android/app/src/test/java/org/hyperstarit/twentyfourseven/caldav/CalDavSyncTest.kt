package org.hyperstarit.twentyfourseven.caldav

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import java.time.ZoneId
import java.time.ZonedDateTime
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.DeviceInfo
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.auth.Tokens
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.hyperstarit.twentyfourseven.sync.CalendarItSync
import org.hyperstarit.twentyfourseven.sync.LocalChanges
import org.hyperstarit.twentyfourseven.sync.SyncRunner
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class CalDavSyncTest {
    private class MemorySecrets : SecretStore {
        val passwords = mutableMapOf<String, String>()
        override suspend fun tokens(accountId: String): Tokens? = null
        override suspend fun saveTokens(accountId: String, tokens: Tokens?) = Unit
        override suspend fun deviceId() = "device-1"
        override suspend fun password(accountId: String) = passwords[accountId]
        override suspend fun savePassword(accountId: String, password: String?) {
            if (password == null) passwords.remove(accountId) else passwords[accountId] = password
        }
    }

    private val fake = FakeCalDav()
    private val secrets = MemorySecrets()
    private lateinit var db: AppDatabase
    private lateinit var accounts: Accounts
    private lateinit var changes: LocalChanges
    private lateinit var runner: SyncRunner

    private val berlin = ZoneId.of("Europe/Berlin")

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java)
            .allowMainThreadQueries().build()
        val device = object : DeviceInfo {
            override val name = "Test"
            override val appVersion = "0"
        }
        accounts = Accounts(db, secrets, OkHttpClient(), Json { ignoreUnknownKeys = true }, Clock.systemUTC(), device)
        changes = LocalChanges(db, Clock.systemUTC()) {}
        val calDav = CalDavSync(db, { accounts.dav(it) }, Clock.systemUTC(), changes, localZone = { berlin })
        runner = SyncRunner(db, accounts, CalendarItSync(db, accounts, Clock.systemUTC(), changes), calDav)
        fake.addCalendar("/cal/me/personal/", "Personal", "VEVENT", "VTODO")
        fake.addCalendar("/cal/me/tasks/", "Errands", "VTODO")
    }

    @After
    fun tearDown() {
        db.close()
        fake.server.close()
    }

    private suspend fun add(): AccountEntity = accounts.addCalDav(fake.url(), "me", "pw", pin = null)

    private fun berlinMillis(y: Int, m: Int, d: Int, h: Int, min: Int = 0) =
        ZonedDateTime.of(y, m, d, h, min, 0, 0, berlin).toInstant().toEpochMilli()

    private val standup = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Nextcloud//EN
        BEGIN:VTIMEZONE
        TZID:Europe/Berlin
        BEGIN:STANDARD
        DTSTART:19701025T030000
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:standup-1
        DTSTAMP:20260101T000000Z
        SUMMARY:Standup
        DESCRIPTION:Daily\, short\nsecond line
        DTSTART;TZID=Europe/Berlin:20261102T090000
        DTEND;TZID=Europe/Berlin:20261102T091500
        RRULE:FREQ=DAILY;COUNT=10
        EXDATE;TZID=Europe/Berlin:20261104T090000
        CATEGORIES:Work,Team
        X-CUSTOM-THING;X-PARAM=1:keep me
        ATTACH:https://files.example/agenda.pdf
        BEGIN:VALARM
        ACTION:DISPLAY
        DESCRIPTION:Standup
        TRIGGER:-PT10M
        END:VALARM
        BEGIN:VALARM
        ACTION:EMAIL
        TRIGGER:-PT1H
        SUMMARY:mail
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:standup-1
        RECURRENCE-ID;TZID=Europe/Berlin:20261103T090000
        DTSTAMP:20260101T000000Z
        SUMMARY:Standup (late)
        DTSTART;TZID=Europe/Berlin:20261103T100000
        DTEND;TZID=Europe/Berlin:20261103T101500
        END:VEVENT
        END:VCALENDAR
    """.trimIndent().replace("\n", "\r\n")

    private fun allDay(uid: String, title: String) = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:$uid
        SUMMARY:$title
        DTSTART;VALUE=DATE:20261224
        DTEND;VALUE=DATE:20261227
        END:VEVENT
        END:VCALENDAR
    """.trimIndent()

    private fun todo(uid: String, title: String, parent: String? = null) = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VTODO
        UID:$uid
        SUMMARY:$title
        DUE;VALUE=DATE:20261201
        PRIORITY:1
        ${parent?.let { "RELATED-TO:$it" } ?: "X-NONE:1"}
        END:VTODO
        END:VCALENDAR
    """.trimIndent()

    @Test
    fun addingAnAccount_FindsTheCalendars_AndTheFirstSyncReadsEverything() = runBlocking {
        fake.put("/cal/me/personal/", "standup.ics", standup)
        fake.put("/cal/me/personal/", "xmas.ics", allDay("xmas", "Christmas"))
        fake.put("/cal/me/tasks/", "child.ics", todo("child", "Buy milk", parent = "parent"))
        fake.put("/cal/me/tasks/", "parent.ics", todo("parent", "Shopping"))
        val account = add()

        assertTrue(runner.syncAll())
        val calendars = db.calendars().forAccount(account.id)
        assertEquals(listOf("Errands", "Personal"), calendars.map { it.name }.sorted())
        val errands = calendars.single { it.name == "Errands" }
        assertFalse(errands.allowsEvents)
        assertTrue(errands.allowsTasks)
        assertEquals("#ff0000", errands.color)

        val events = db.events().observeLive().first().map { it.data }
        val s = events.single { it.title == "Standup" }
        assertEquals("Daily, short\nsecond line", s.description)
        assertEquals(berlinMillis(2026, 11, 2, 9), s.start)
        assertEquals("Europe/Berlin", s.timeZone)
        assertEquals("FREQ=DAILY;COUNT=10", s.recurrence)
        // Deleted and edited occurrences are both excluded from the rule.
        assertEquals(listOf(berlinMillis(2026, 11, 3, 9), berlinMillis(2026, 11, 4, 9)), s.exDates)
        assertEquals(listOf(Reminder(10, "WebPush")), s.reminders) // the email alarm is the server's
        assertEquals("dav:Work", s.categoryId)
        val o = db.events().observeLiveOverrides().first().single().data
        assertEquals("Standup (late)", o.title)
        assertEquals(berlinMillis(2026, 11, 3, 10), o.start)

        val xmas = events.single { it.title == "Christmas" }
        assertTrue(xmas.allDay)
        assertEquals(java.time.LocalDate.of(2026, 12, 24).toEpochDay() * 86_400_000L, xmas.start)
        assertEquals(java.time.LocalDate.of(2026, 12, 26).toEpochDay() * 86_400_000L, xmas.end) // inclusive last day

        val tasks = db.tasks().observeLive().first().map { it.data }
        val parent = tasks.single { it.title == "Shopping" }
        assertEquals(parent.id, tasks.single { it.title == "Buy milk" }.parentId) // linked though it came first
        assertEquals("high", parent.priority)
        assertTrue(parent.dueAllDay)

        assertEquals(listOf("Work"), db.categories().observeAll().first().filter { it.accountId == account.id }.map { it.name })
    }

    @Test
    fun anEdit_IsPutOnTheVersionEdited_AndKeepsWhatTheAppDoesntKnow() = runBlocking {
        fake.put("/cal/me/personal/", "standup.ics", standup)
        add()
        runner.syncAll()
        val s = db.events().observeLive().first().single().data
        changes.updateEvent(s.copy(title = "Daily standup"))
        runner.syncAll()

        val put = fake.requests.last { it.method == "PUT" }
        assertEquals("\"e1\"", put.headers["If-Match"])
        val ics = fake.ics("/cal/me/personal/", "standup.ics")!!
        assertTrue(ics.contains("SUMMARY:Daily standup"))
        assertTrue(ics.contains("X-CUSTOM-THING;X-PARAM=1:keep me"))
        assertTrue(ics.contains("ATTACH:https://files.example/agenda.pdf"))
        assertTrue(ics.contains("ACTION:EMAIL"))
        assertTrue(ics.contains("RECURRENCE-ID;TZID=Europe/Berlin:20261103T090000"))
        // Only the deleted occurrence is an EXDATE; the edited one is its own VEVENT.
        assertTrue(ics.contains("EXDATE;TZID=Europe/Berlin:20261104T090000\r\n"))
        assertEquals(0, db.outbox().forAccount(s.accountId).size)
        assertEquals("Daily standup", db.events().observeLive().first().single().data.title)
        assertNull(db.events().edit(s.accountId, s.id))
    }

    @Test
    fun aChangeMadeElsewhereFirst_WinsAndTheUserIsTold() = runBlocking {
        fake.put("/cal/me/personal/", "standup.ics", standup)
        add()
        runner.syncAll()
        val s = db.events().observeLive().first().single().data
        fake.put("/cal/me/personal/", "standup.ics", standup.replace("SUMMARY:Standup\r\n", "SUMMARY:Renamed on the web\r\n"))
        changes.updateEvent(s.copy(title = "Mine"))
        runner.syncAll()

        assertEquals("Renamed on the web", db.events().observeLive().first().single().data.title)
        val notice = db.notices().observeAll().first().single()
        assertEquals("conflict", notice.kind)
        assertNotNull(notice.op) // "apply mine anyway" is offered

        changes.reapply(notice.id)
        runner.syncAll()
        assertEquals("Mine", db.events().observeLive().first().single().data.title)
        assertTrue(fake.ics("/cal/me/personal/", "standup.ics")!!.contains("SUMMARY:Mine"))
    }

    @Test
    fun created_Moved_AndDeletedEvents_ReachTheServer() = runBlocking {
        val account = add()
        runner.syncAll()
        val personal = "/cal/me/personal/"
        val start = berlinMillis(2026, 12, 1, 18)
        val id = changes.createEvent(
            EventData(
                id = "", accountId = account.id, calendarId = personal, title = "Dinner", description = null, location = "Home",
                categoryId = null, color = null, start = start, end = start + 7_200_000, allDay = false, timeZone = "Europe/Berlin",
                recurrence = null, exDates = emptyList(), reminders = listOf(Reminder(30, "WebPush")), attendees = emptyList(),
                invitationStatus = null, organizerEmail = null, version = null,
            ),
        )
        runner.syncAll()
        val put = fake.requests.last { it.method == "PUT" }
        assertEquals("*", put.headers["If-None-Match"])
        val ics = fake.ics(personal, "$id.ics")!!
        assertTrue(ics.contains("DTSTART;TZID=Europe/Berlin:20261201T180000"))
        assertTrue(ics.contains("BEGIN:VTIMEZONE")) // a TZID needs its definition
        assertTrue(ics.contains("TRIGGER:-PT30M"))
        assertNotNull(db.events().server(account.id, id)?.data?.version)

        fake.addCalendar("/cal/me/work/", "Work", "VEVENT")
        runner.syncAll()
        changes.updateEvent(db.events().live(account.id, id)!!.data.copy(calendarId = "/cal/me/work/"))
        runner.syncAll()
        assertNull(fake.ics(personal, "$id.ics"))
        assertNotNull(fake.ics("/cal/me/work/", "$id.ics"))
        assertEquals("/cal/me/work/", db.events().live(account.id, id)!!.data.calendarId)

        changes.deleteEvent(account.id, id)
        runner.syncAll()
        assertNull(fake.ics("/cal/me/work/", "$id.ics"))
        assertTrue(db.events().observeLive().first().isEmpty())
    }

    @Test
    fun theNextSync_FetchesOnlyWhatChanged_ByItsToken() = runBlocking {
        fake.put("/cal/me/personal/", "a.ics", allDay("a", "A"))
        fake.put("/cal/me/personal/", "b.ics", allDay("b", "B"))
        add()
        runner.syncAll()
        fake.remove("/cal/me/personal/", "a.ics")
        fake.put("/cal/me/personal/", "b.ics", allDay("b", "B2"))
        fake.requests.clear()
        runner.syncAll()

        assertEquals(listOf("B2"), db.events().observeLive().first().map { it.data.title })
        val report = fake.requests.first { it.method == "REPORT" && it.url.encodedPath == "/cal/me/personal/" }
        assertTrue(report.body!!.utf8().contains("<d:sync-token>tok-2</d:sync-token>"))
        // The unchanged task list isn't listed at all.
        assertTrue(fake.requests.none { it.method == "REPORT" && it.url.encodedPath == "/cal/me/tasks/" })
    }

    @Test
    fun withoutSyncTokens_EtagsAreCompared() = runBlocking {
        fake.supportsSync = false
        fake.put("/cal/me/personal/", "a.ics", allDay("a", "A"))
        fake.put("/cal/me/personal/", "b.ics", allDay("b", "B"))
        add()
        runner.syncAll()
        fake.remove("/cal/me/personal/", "a.ics")
        runner.syncAll()
        assertEquals(listOf("B"), db.events().observeLive().first().map { it.data.title })
    }

    @Test
    fun anOccurrenceDeleted_BecomesAnExdate_AndTicksReachTheServer() = runBlocking {
        fake.put("/cal/me/personal/", "standup.ics", standup)
        fake.put("/cal/me/tasks/", "parent.ics", todo("parent", "Shopping"))
        val account = add()
        runner.syncAll()
        val s = db.events().observeLive().first().single().data
        changes.deleteOccurrence(account.id, s.id, berlinMillis(2026, 11, 6, 9))
        val t = db.tasks().observeLive().first().single().data
        changes.setTaskDone(account.id, t.id, true)
        runner.syncAll()

        assertTrue(fake.ics("/cal/me/personal/", "standup.ics")!!.contains("EXDATE;TZID=Europe/Berlin:20261104T090000,20261106T090000"))
        val todo = fake.ics("/cal/me/tasks/", "parent.ics")!!
        assertTrue(todo.contains("STATUS:COMPLETED"))
        assertTrue(todo.contains("COMPLETED:"))
        assertEquals("Completed", db.tasks().observeLive().first().single().data.status)
    }

    @Test
    fun digestLogins_Work() = runBlocking {
        fake.digest = true
        fake.put("/cal/me/personal/", "a.ics", allDay("a", "A"))
        add()
        assertTrue(runner.syncAll())
        assertEquals(listOf("A"), db.events().observeLive().first().map { it.data.title })
    }

    @Test
    fun aWrongPassword_IsRefused_AndARefusedSyncAsksToSignInAgain() = runBlocking {
        try {
            accounts.addCalDav(fake.url(), "me", "wrong", pin = null)
            fail("signed in with a wrong password")
        } catch (_: DavAuthException) {
        }
        val account = add()
        secrets.passwords[account.id] = "changed elsewhere"
        runner.syncAll()
        assertTrue(db.accounts().get(account.id)!!.needsSignIn)
        val before = fake.requests.size
        runner.syncAll()
        assertEquals(before, fake.requests.size) // nothing is sent until the user signs in

        accounts.signInAgain(account.id, "pw")
        assertFalse(db.accounts().get(account.id)!!.needsSignIn)
        assertTrue(runner.syncAll())
    }

    @Test
    fun removingTheAccount_LeavesNothingBehind() = runBlocking {
        fake.put("/cal/me/personal/", "a.ics", allDay("a", "A"))
        val account = add()
        runner.syncAll()
        accounts.remove(account.id)
        assertTrue(db.events().observeLive().first().isEmpty())
        assertNull(db.dav().account(account.id))
        assertTrue(db.dav().calendars(account.id).isEmpty())
        assertNull(secrets.passwords[account.id])
    }
}
