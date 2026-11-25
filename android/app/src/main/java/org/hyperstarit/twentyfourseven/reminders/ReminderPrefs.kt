package org.hyperstarit.twentyfourseven.reminders

import android.content.Context
import androidx.core.content.edit

/**
 * Device-only reminder state, in plain SharedPreferences (read synchronously when an alarm fires):
 * whether reminders ring here, which alarms are set, and which reminders already rang (so a
 * reschedule never rings one twice). Nothing in it is secret: keys are ids and times.
 */
class ReminderPrefs(context: Context) {
    private val prefs = context.getSharedPreferences("reminders", Context.MODE_PRIVATE)

    var enabled: Boolean
        get() = prefs.getBoolean(ENABLED, true)
        set(value) = prefs.edit { putBoolean(ENABLED, value) }

    /** Whether the app already asked for the notification permission once. */
    var askedForPermission: Boolean
        get() = prefs.getBoolean(ASKED, false)
        set(value) = prefs.edit { putBoolean(ASKED, value) }

    var scheduled: Set<String>
        get() = prefs.getStringSet(SCHEDULED, emptySet()).orEmpty()
        set(value) = prefs.edit { putStringSet(SCHEDULED, value) }

    @Synchronized
    fun fired(): Set<String> = prefs.getStringSet(FIRED, emptySet()).orEmpty().map { it.substringBeforeLast('|') }.toSet()

    /** Notes [key] as rung; forgets what rang more than three days ago. */
    @Synchronized
    fun markFired(key: String, now: Long) {
        val keep = prefs.getStringSet(FIRED, emptySet()).orEmpty()
            .filter { (it.substringAfterLast('|').toLongOrNull() ?: 0) > now - KEEP_FIRED }
        prefs.edit { putStringSet(FIRED, (keep + "$key|$now").toSet()) }
    }

    private companion object {
        const val ENABLED = "enabled"
        const val ASKED = "asked_permission"
        const val SCHEDULED = "scheduled"
        const val FIRED = "fired"
        const val KEEP_FIRED = 3 * 86_400_000L
    }
}
