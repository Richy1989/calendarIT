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
        } else {
            putString("$accountId.url", push.endpoint); putString("$accountId.p256dh", push.p256dh); putString("$accountId.auth", push.auth)
        }
    }

    fun hasAny(): Boolean = prefs.all.keys.any { it.endsWith(".url") }
}
