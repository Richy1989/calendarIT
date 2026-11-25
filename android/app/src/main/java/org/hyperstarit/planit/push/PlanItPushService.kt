package org.hyperstarit.planit.push

import android.util.Log
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.planit.net.DevicePushInput
import org.hyperstarit.planit.sync.SyncScheduler
import org.unifiedpush.android.connector.FailedReason
import org.unifiedpush.android.connector.PushService
import org.unifiedpush.android.connector.data.PushEndpoint
import org.unifiedpush.android.connector.data.PushMessage

/**
 * Receives UnifiedPush events from the distributor. A message is only ever "sync" (the server sends
 * no content); whatever it says, the answer is the same: sync now.
 */
@AndroidEntryPoint
class PlanItPushService : PushService() {
    @Inject lateinit var registrar: PushRegistrar
    @Inject lateinit var scheduler: SyncScheduler
    @Inject lateinit var scope: CoroutineScope

    override fun onMessage(message: PushMessage, instance: String) {
        scheduler.requestNow()
    }

    override fun onNewEndpoint(endpoint: PushEndpoint, instance: String) {
        val keys = endpoint.pubKeySet
        val push = DevicePushInput(kind = "unifiedpush", endpoint = endpoint.url, p256dh = keys?.pubKey, auth = keys?.auth)
        scope.launch { registrar.endpointFor(instance, push) }
    }

    override fun onRegistrationFailed(reason: FailedReason, instance: String) {
        Log.w("PlanItPush", "Distributor refused the registration: $reason")
    }

    override fun onUnregistered(instance: String) {
        scope.launch { registrar.endpointFor(instance, null) }
    }
}
