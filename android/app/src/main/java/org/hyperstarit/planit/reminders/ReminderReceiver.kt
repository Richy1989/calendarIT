package org.hyperstarit.planit.reminders

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.text.format.DateUtils
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.net.toUri
import dagger.hilt.android.AndroidEntryPoint
import java.time.Clock
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.planit.MainActivity
import org.hyperstarit.planit.R
import org.hyperstarit.planit.sync.LocalChanges

/** Rings a reminder (a notification), and handles its buttons: snooze, and done for a task. */
@AndroidEntryPoint
class ReminderReceiver : BroadcastReceiver() {
    @Inject lateinit var prefs: ReminderPrefs
    @Inject lateinit var scheduler: ReminderScheduler
    @Inject lateinit var changes: LocalChanges
    @Inject lateinit var scope: CoroutineScope
    @Inject lateinit var clock: Clock

    override fun onReceive(context: Context, intent: Intent) {
        val key = intent.getStringExtra(EXTRA_KEY) ?: return
        val notificationId = key.removeSuffix(":snooze").hashCode()
        when (intent.action) {
            ACTION_FIRE -> {
                prefs.markFired(key, clock.millis())
                if (prefs.enabled) notify(context, intent, notificationId)
            }
            ACTION_SNOOZE -> {
                NotificationManagerCompat.from(context).cancel(notificationId)
                scheduler.snooze(intent, SNOOZE_MINUTES)
            }
            ACTION_DONE -> {
                NotificationManagerCompat.from(context).cancel(notificationId)
                val taskId = intent.getStringExtra(EXTRA_ITEM) ?: return
                val pending = goAsync()
                scope.launch {
                    try { changes.setTaskDone(taskId, true) } finally { pending.finish() }
                }
            }
        }
    }

    private fun notify(context: Context, intent: Intent, id: Int) {
        val manager = NotificationManagerCompat.from(context)
        if (!manager.areNotificationsEnabled()) return
        val title = intent.getStringExtra(EXTRA_TITLE).orEmpty()
        val begins = intent.getLongExtra(EXTRA_BEGINS, 0)
        val allDay = intent.getBooleanExtra(EXTRA_ALL_DAY, false)
        val task = intent.getStringExtra(EXTRA_KIND) == PlannedReminder.Kind.TASK.name
        val whenText = if (allDay && !task) {
            DateUtils.formatDateTime(context, begins, DateUtils.FORMAT_SHOW_DATE or DateUtils.FORMAT_SHOW_WEEKDAY)
        } else {
            DateUtils.formatDateTime(context, begins, DateUtils.FORMAT_SHOW_TIME or DateUtils.FORMAT_SHOW_DATE or DateUtils.FORMAT_SHOW_WEEKDAY)
        }
        val text = (if (task) "Due $whenText" else whenText) + (intent.getStringExtra(EXTRA_LOCATION)?.let { " · $it" } ?: "")
        val open = PendingIntent.getActivity(
            context, id, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setCategory(if (task) NotificationCompat.CATEGORY_REMINDER else NotificationCompat.CATEGORY_EVENT)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setContentIntent(open)
            .setAutoCancel(true)
            .addAction(0, "Snooze $SNOOZE_MINUTES min", action(context, intent, ACTION_SNOOZE, id))
        if (task) builder.addAction(0, "Done", action(context, intent, ACTION_DONE, id))
        try {
            manager.notify(id, builder.build())
        } catch (_: SecurityException) {
            // The permission was taken back between the check and now: nothing to show.
        }
    }

    private fun action(context: Context, from: Intent, action: String, id: Int): PendingIntent =
        PendingIntent.getBroadcast(
            context, id, Intent(from).setAction(action).setData("planit://reminder/${from.getStringExtra(EXTRA_KEY)}/$action".toUri()),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

    companion object {
        const val CHANNEL = "reminders"
        const val ACTION_FIRE = "org.hyperstarit.planit.REMINDER"
        const val ACTION_SNOOZE = "org.hyperstarit.planit.REMINDER_SNOOZE"
        const val ACTION_DONE = "org.hyperstarit.planit.REMINDER_DONE"
        const val EXTRA_KEY = "key"
        const val EXTRA_KIND = "kind"
        const val EXTRA_ITEM = "item"
        const val EXTRA_TITLE = "title"
        const val EXTRA_BEGINS = "begins"
        const val EXTRA_ALL_DAY = "allDay"
        const val EXTRA_LOCATION = "location"
        const val SNOOZE_MINUTES = 10

        /** Everything the notification needs rides along: firing reads nothing from the database. */
        fun fireIntent(context: Context, r: PlannedReminder): Intent =
            Intent(context, ReminderReceiver::class.java)
                .setAction(ACTION_FIRE)
                .setData("planit://reminder/${r.key}".toUri())
                .putExtra(EXTRA_KEY, r.key)
                .putExtra(EXTRA_KIND, r.kind.name)
                .putExtra(EXTRA_ITEM, r.itemId)
                .putExtra(EXTRA_TITLE, r.title)
                .putExtra(EXTRA_BEGINS, r.begins)
                .putExtra(EXTRA_ALL_DAY, r.allDay)
                .putExtra(EXTRA_LOCATION, r.location)

        fun createChannel(context: Context) {
            val channel = NotificationChannel(CHANNEL, "Reminders", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Reminders for your appointments and tasks"
            }
            context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }
}

/** Alarms don't survive a reboot, and depend on the clock: set them again when either changes. */
@AndroidEntryPoint
class RescheduleReceiver : BroadcastReceiver() {
    @Inject lateinit var scheduler: ReminderScheduler
    @Inject lateinit var scope: CoroutineScope

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action !in SYSTEM_EVENTS) return
        val pending = goAsync()
        scope.launch {
            try { scheduler.reschedule() } finally { pending.finish() }
        }
    }

    private companion object {
        val SYSTEM_EVENTS = setOf(
            Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED, Intent.ACTION_TIME_CHANGED, Intent.ACTION_TIMEZONE_CHANGED,
        )
    }
}
