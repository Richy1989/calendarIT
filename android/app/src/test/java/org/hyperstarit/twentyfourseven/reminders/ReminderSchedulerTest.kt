package org.hyperstarit.twentyfourseven.reminders

import android.app.AlarmManager
import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import kotlinx.coroutines.runBlocking
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.EventEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class ReminderSchedulerTest {
    private val context: Context = ApplicationProvider.getApplicationContext()
    private lateinit var db: AppDatabase
    private lateinit var prefs: ReminderPrefs
    private lateinit var scheduler: ReminderScheduler
    private val alarms get() = shadowOf(context.getSystemService(AlarmManager::class.java)).scheduledAlarms
    // A fixed moment (the one a CI run once failed at), so no test depends on the wall clock.
    private val now = java.time.Instant.parse("2026-11-25T20:55:44.551Z").toEpochMilli()
    private val clock = Clock.fixed(java.time.Instant.ofEpochMilli(now), java.time.ZoneOffset.UTC)

    @Before
    fun setUp() = runBlocking {
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java).allowMainThreadQueries().build()
        prefs = ReminderPrefs(context)
        scheduler = ReminderScheduler(context, db, prefs, clock)
        db.calendars().upsert(listOf(CalendarEntity("cal", "a", "P", null, null, null, emptyList(), false, 0, "1")))
    }

    @After
    fun tearDown() = db.close()

    private suspend fun event(id: String, startsIn: Long, reminders: List<Reminder>, rule: String? = null) = db.events().upsert(
        listOf(
            EventEntity(
                EventData(
                    id, "a", "cal", id, null, null, null, null, now + startsIn, now + startsIn + 3_600_000, false, "UTC", rule,
                    emptyList(), reminders, emptyList(), null, null, "1",
                ),
            ),
        ),
    )

    @Test
    fun upcomingReminders_GetExactAlarms_AndGoneOnesAreCancelled() = runBlocking {
        event("a", 2 * 3_600_000L, listOf(Reminder(10, "WebPush"), Reminder(30, "Email")))
        event("b", 3 * 3_600_000L, listOf(Reminder(5, "WebPush")))
        scheduler.reschedule()
        assertEquals(2, alarms.size)
        assertEquals(now + 2 * 3_600_000L - 10 * 60_000L, alarms.minOf { it.triggerAtTime })

        db.events().deleteServer(listOf("b"))
        scheduler.reschedule()
        assertEquals(1, alarms.size)
    }

    @Test
    fun aRepeatingSeries_IsSetForTheComingDays_Only() = runBlocking {
        event("daily", 3_600_000L, listOf(Reminder(0, "WebPush")), rule = "FREQ=DAILY")
        scheduler.reschedule()
        assertEquals(ReminderScheduler.HORIZON_DAYS.toInt(), alarms.size) // today's and the next days' up to the horizon
    }

    @Test
    fun turnedOff_NothingRings() = runBlocking {
        event("a", 3_600_000L, listOf(Reminder(10, "WebPush")))
        prefs.enabled = false
        scheduler.reschedule()
        assertEquals(0, alarms.size)
        prefs.enabled = true
    }

    @Test
    fun oneThatAlreadyRang_IsNotSetAgain() = runBlocking {
        event("a", 3_600_000L, listOf(Reminder(10, "WebPush")))
        scheduler.reschedule()
        prefs.markFired(prefs.scheduled.single(), now)
        scheduler.reschedule()
        assertEquals(0, alarms.size)
    }
}
