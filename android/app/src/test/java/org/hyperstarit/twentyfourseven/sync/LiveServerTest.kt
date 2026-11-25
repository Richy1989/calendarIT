package org.hyperstarit.twentyfourseven.sync

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.DeviceInfo
import org.hyperstarit.twentyfourseven.accounts.UntrustedCertificateException
import org.hyperstarit.twentyfourseven.auth.LegacySession
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.auth.Tokens
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.hyperstarit.twentyfourseven.data.db.TaskData
import org.hyperstarit.twentyfourseven.net.Certificates
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The app against a real backend: runs only with LIVE_SERVER_URL, LIVE_SERVER_EMAIL and
 * LIVE_SERVER_PASSWORD set (a throwaway account — it changes its data). Checks that what the app
 * sends is what the server takes.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class LiveServerTest {
    private val url = System.getenv("LIVE_SERVER_URL")
    private val email = System.getenv("LIVE_SERVER_EMAIL")
    private val password = System.getenv("LIVE_SERVER_PASSWORD")

    @Test
    fun changesMadeOnTheDevice_ReachTheServer_AndAClashIsReported() = runBlocking {
        assumeTrue(url != null && email != null && password != null)
        val db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java)
            .allowMainThreadQueries().build()
        val secrets = object : SecretStore {
            val map = mutableMapOf<String, Tokens>()
            override suspend fun tokens(accountId: String) = map[accountId]
            override suspend fun saveTokens(accountId: String, tokens: Tokens?) { if (tokens == null) map.remove(accountId) else map[accountId] = tokens }
            override suspend fun deviceId() = "live-test-device-0000-000000000000".let { "5b0c9a3e-1f0a-4c55-9d1e-0a7c1e0f0001" }
            override suspend fun takeLegacySession(): LegacySession? = null
        }
        val device = object : DeviceInfo {
            override val name = "Live test"
            override val appVersion = "test"
        }
        val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
        val accounts = Accounts(db, secrets, OkHttpClient(), json, Clock.systemUTC(), device)
        val changes = LocalChanges(db, Clock.systemUTC()) {}
        val runner = SyncRunner(db, accounts, CalendarItSync(db, accounts, Clock.systemUTC(), changes))
        val server = url!!.toHttpUrl()

        val account = try {
            accounts.addCalendarIt(server, email!!, password!!, pin = null)
        } catch (e: UntrustedCertificateException) {
            accounts.addCalendarIt(server, email!!, password!!, pin = e.certificate.sha256)
        }
        assertTrue(runner.syncAll())
        val calendar = db.calendars().forAccount(account.id).first().id

        // Create, then edit before it was ever sent (folded into the create), a task ticked off,
        // and a rename of an existing series.
        val start = java.time.Instant.parse("2026-12-03T10:00:00Z").toEpochMilli()
        val created = changes.createEvent(
            org.hyperstarit.twentyfourseven.data.db.EventData(
                id = "", accountId = account.id, calendarId = calendar, title = "From the phone", description = "notes",
                location = null, categoryId = null, color = null, start = start, end = start + 3_600_000, allDay = false,
                timeZone = "Europe/Berlin", recurrence = null, exDates = emptyList(),
                reminders = listOf(Reminder(15, "WebPush")), attendees = emptyList(), invitationStatus = null,
                organizerEmail = null, version = null,
            ),
        )
        changes.updateEvent(db.events().live(created)!!.data.copy(title = "From the phone, renamed"))
        val task = changes.createTask(
            TaskData("", account.id, calendar, "Phone task", null, null, false, null, "NeedsAction", null, "high", null, 0, emptyList(), null),
        )
        changes.setTaskDone(task, true)
        val standup = db.events().observeLive().first().first { it.data.title.startsWith("Standup") }.data
        changes.updateEvent(standup.copy(title = "Standup (from phone)"))

        assertTrue(runner.syncAll())
        assertTrue(db.outbox().forAccount(account.id).isEmpty())
        assertTrue(db.notices().observeAll().first().isEmpty())
        val titles = db.events().observeLive().first().map { it.data.title }
        assertTrue(titles.toString(), "From the phone, renamed" in titles && "Standup (from phone)" in titles)
        assertEquals("Completed", db.tasks().live(task)!!.data.status)
        assertEquals(1, db.events().observeLiveOverrides().first().count { it.data.seriesId == standup.id }) // kept: not moved

        // Someone else renames the series on the server; the phone's rename on the old version clashes.
        val token = secrets.map[account.id]!!.accessToken
        val http = Certificates.clientFor(OkHttpClient(), account.certificatePin)
        val dto = http.newCall(Request.Builder().url(server.resolve("api/events/${standup.id}")!!).header("Authorization", "Bearer $token").build())
            .execute().use { Json.parseToJsonElement(it.body.string()).jsonObject }
        val body = """{"title":"Standup (web)","start":"${dto["start"]!!.jsonPrimitive.content}","end":"${dto["end"]!!.jsonPrimitive.content}","recurrence":"${dto["recurrence"]!!.jsonPrimitive.content}"}"""
        http.newCall(
            Request.Builder().url(server.resolve("api/events/${standup.id}")!!).header("Authorization", "Bearer $token")
                .put(body.toRequestBody("application/json".toMediaType())).build(),
        ).execute().use { assertEquals(200, it.code) }
        changes.updateEvent(db.events().live(standup.id)!!.data.copy(title = "Standup (phone again)"))
        runner.syncAll()
        assertEquals("Standup (web)", db.events().live(standup.id)!!.data.title)
        assertEquals("conflict", db.notices().observeAll().first().single().kind)

        // "Apply mine anyway" goes through on the server's version.
        changes.reapply(db.notices().observeAll().first().single().id)
        runner.syncAll()
        assertEquals("Standup (phone again)", db.events().live(standup.id)!!.data.title)

        changes.deleteEvent(created)
        runner.syncAll()
        assertTrue(db.events().live(created) == null)
        db.close()
    }
}
