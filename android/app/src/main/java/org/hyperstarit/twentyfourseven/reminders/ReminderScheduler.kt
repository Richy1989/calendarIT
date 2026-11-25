package org.hyperstarit.twentyfourseven.reminders

import android.annotation.SuppressLint
import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.net.toUri
import java.time.Clock
import java.time.LocalDate
import java.time.ZoneId
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.hyperstarit.twentyfourseven.calendar.DateRange
import org.hyperstarit.twentyfourseven.calendar.Occurrences
import org.hyperstarit.twentyfourseven.data.db.AppDatabase

/**
 * Keeps the phone's alarms in step with its data: after every sync and change, at app start, at
 * boot and when the clock or zone changes. Alarms are exact (calendar apps may use
 * USE_EXACT_ALARM) and wake the device; the next [HORIZON_DAYS] days are set, at most [MAX_ALARMS]
 * (Android caps an app's alarms), and the periodic sync keeps the horizon moving.
 */
class ReminderScheduler(
    private val context: Context,
    private val db: AppDatabase,
    private val prefs: ReminderPrefs,
    private val clock: Clock,
) {
    private val mutex = Mutex()
    private val alarms = context.getSystemService(AlarmManager::class.java)

    suspend fun reschedule() = mutex.withLock {
        withContext(Dispatchers.Default) {
            val now = clock.millis()
            val planned = if (prefs.enabled) plan(now) else emptyList()
            val fired = prefs.fired()
            val keep = HashSet<String>()
            for (r in planned) {
                if (r.key in fired) continue
                // One missed while the phone was off (or the alarm late) still rings, at once.
                set(r, maxOf(r.at, now + 1_000))
                keep += r.key
            }
            prefs.scheduled.filter { it !in keep }.forEach(::cancel)
            prefs.scheduled = keep
        }
    }

    private suspend fun plan(now: Long): List<PlannedReminder> {
        val zone = ZoneId.systemDefault()
        val today = LocalDate.now(clock.withZone(zone))
        val calendars = db.calendars().observeAll().first().associateBy { it.id }
        val occurrences = Occurrences.build(
            db.events().observeLive().first().map { it.data },
            db.events().observeLiveOverrides().first().map { it.data },
            calendars,
            db.categories().observeAll().first().associateBy { it.id },
            hiddenCalendars = emptySet(), // hiding a calendar from view doesn't silence it, as on the web
            range = DateRange(today.minusDays(2), today.plusDays(HORIZON_DAYS + 2)),
            zone = zone,
        )
        val tasks = db.tasks().observeLive().first().map { it.data }
        return ReminderPlanner.plan(occurrences, tasks, calendars, now - MISSED_GRACE, now + HORIZON_DAYS * 86_400_000L, zone)
            .take(MAX_ALARMS)
    }

    /** Snooze: the same reminder again in [minutes]. */
    fun snooze(intent: Intent, minutes: Int) {
        val key = intent.getStringExtra(ReminderReceiver.EXTRA_KEY) ?: return
        val again = Intent(intent).setAction(ReminderReceiver.ACTION_FIRE)
            .putExtra(ReminderReceiver.EXTRA_KEY, "$key:snooze")
            .setData("twentyfourseven://reminder/$key:snooze".toUri())
        setAlarm(again, clock.millis() + minutes * 60_000L)
    }

    private fun set(r: PlannedReminder, at: Long) = setAlarm(ReminderReceiver.fireIntent(context, r), at)

    // USE_EXACT_ALARM (a calendar app's, granted at install) covers it; canScheduleExactAlarms()
    // guards the case the system still says no.
    @SuppressLint("MissingPermission")
    private fun setAlarm(intent: Intent, at: Long) {
        val pending = PendingIntent.getBroadcast(context, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        if (alarms.canScheduleExactAlarms()) {
            alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, pending)
        } else {
            alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, pending)
        }
    }

    private fun cancel(key: String) {
        val intent = Intent(context, ReminderReceiver::class.java).setAction(ReminderReceiver.ACTION_FIRE).setData("twentyfourseven://reminder/$key".toUri())
        PendingIntent.getBroadcast(context, 0, intent, PendingIntent.FLAG_NO_CREATE or PendingIntent.FLAG_IMMUTABLE)?.let {
            alarms.cancel(it)
            it.cancel()
        }
    }

    companion object {
        const val HORIZON_DAYS = 8L
        const val MAX_ALARMS = 200
        /** A reminder this late (the phone was off) still rings; older ones are let go. */
        const val MISSED_GRACE = 15 * 60_000L
    }
}
