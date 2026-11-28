package org.hyperstarit.twentyfourseven.push

import android.util.Log
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.net.DevicePushInput
import org.hyperstarit.twentyfourseven.sync.SyncScheduler
import org.unifiedpush.android.connector.FailedReason
import org.unifiedpush.android.connector.PushService
import org.unifiedpush.android.connector.data.PushEndpoint
import org.unifiedpush.android.connector.data.PushMessage

/**
 * Receives UnifiedPush events from the distributor. A message carries no calendar content: it is
 * "sync" (sync now) or "test" (only noted, for Settings → Instant updates).
 */
@AndroidEntryPoint
class SyncPushService : PushService() {
    @Inject lateinit var registrar: PushRegistrar
    @Inject lateinit var scheduler: SyncScheduler
    @Inject lateinit var scope: CoroutineScope
    @Inject lateinit var prefs: PushPrefs

    /** "sync" (something changed: sync now) or "test" (Settings → Instant updates asked for one). */
    override fun onMessage(message: PushMessage, instance: String) {
        val test = message.decrypted && message.content.decodeToString().contains("\"test\"")
        prefs.received(instance, test)
        if (!test) scheduler.requestNow()
    }

    override fun onNewEndpoint(endpoint: PushEndpoint, instance: String) {
        val keys = endpoint.pubKeySet
        val push = DevicePushInput(kind = "unifiedpush", endpoint = endpoint.url, p256dh = keys?.pubKey, auth = keys?.auth)
        scope.launch { registrar.endpointFor(instance, push) }
    }

    override fun onRegistrationFailed(reason: FailedReason, instance: String) {
        Log.w("24sevenPush", "Distributor refused the registration: $reason")
    }

    override fun onUnregistered(instance: String) {
        scope.launch { registrar.endpointFor(instance, null) }
    }
}
