package org.hyperstarit.twentyfourseven.push

import android.content.Context
import androidx.core.content.edit
import org.hyperstarit.twentyfourseven.net.DevicePushInput

/**
 * Each account's UnifiedPush endpoint (one per account: every server signs its pushes with its own
 * key). The endpoint is a capability URL — whoever has it can wake the app — so it stays in the
 * app's private storage and is only ever sent to that account's own server.
 */
class PushPrefs(context: Context) {
    private val prefs = context.getSharedPreferences("push", Context.MODE_PRIVATE)

    fun endpoint(accountId: String): DevicePushInput? {
        val url = prefs.getString("$accountId.url", null) ?: return null
        return DevicePushInput(kind = "unifiedpush", endpoint = url, p256dh = prefs.getString("$accountId.p256dh", null), auth = prefs.getString("$accountId.auth", null))
    }

    fun setEndpoint(accountId: String, push: DevicePushInput?) = prefs.edit {
        if (push == null) {
            remove("$accountId.url"); remove("$accountId.p256dh"); remove("$accountId.auth")
            remove("$accountId.lastPush"); remove("$accountId.lastTest")
        } else {
            putString("$accountId.url", push.endpoint); putString("$accountId.p256dh", push.p256dh); putString("$accountId.auth", push.auth)
        }
    }

    /** When a push last arrived for [accountId] (epoch ms), and when a test push last did. */
    fun lastPush(accountId: String): Long? = prefs.getLong("$accountId.lastPush", 0L).takeIf { it > 0 }
    fun lastTest(accountId: String): Long? = prefs.getLong("$accountId.lastTest", 0L).takeIf { it > 0 }

    fun received(accountId: String, test: Boolean, at: Long = System.currentTimeMillis()) = prefs.edit {
        putLong("$accountId.lastPush", at)
        if (test) putLong("$accountId.lastTest", at)
    }

    /** Calls [onChange] whenever something here changes, until the returned function is called. */
    fun observe(onChange: () -> Unit): () -> Unit {
        val listener = android.content.SharedPreferences.OnSharedPreferenceChangeListener { _, _ -> onChange() }
        prefs.registerOnSharedPreferenceChangeListener(listener)
        return { prefs.unregisterOnSharedPreferenceChangeListener(listener) }
    }

    /** The user turned instant updates off: the app no longer picks a distributor by itself at start. */
    var turnedOff: Boolean
        get() = prefs.getBoolean("turnedOff", false)
        set(value) = prefs.edit { putBoolean("turnedOff", value) }

    fun hasAny(): Boolean = prefs.all.keys.any { it.endsWith(".url") }
}
