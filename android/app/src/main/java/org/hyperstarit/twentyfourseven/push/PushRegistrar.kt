package org.hyperstarit.twentyfourseven.push

import android.content.Context
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.data.db.AccountKind
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.unifiedpush.android.connector.UnifiedPush

/**
 * Instant updates through UnifiedPush — no Google services: the user's distributor app (e.g. ntfy)
 * holds the connection, each CalendarIT server pushes a content-free "sync" to it, and the app syncs.
 * Without a distributor nothing changes: the app keeps syncing on its schedule and when opened.
 */
class PushRegistrar(
    private val context: Context,
    private val db: AppDatabase,
    private val accounts: Accounts,
    private val prefs: PushPrefs,
) {
    /** The distributor apps installed (package names). */
    fun distributors(): List<String> = UnifiedPush.getDistributors(context)

    /** The distributor in use, or null. */
    fun current(): String? = if (prefs.turnedOff) null else runCatching {
        UnifiedPush.getAckDistributor(context) ?: UnifiedPush.getSavedDistributor(context)
    }.getOrNull()

    val active: Boolean get() = prefs.hasAny()

    /** Off by the user's choice (Settings → Instant updates): nothing registers by itself then. */
    val turnedOff: Boolean get() = prefs.turnedOff

    /** The user turned it on (after picking a distributor). */
    suspend fun turnOn() {
        prefs.turnedOff = false
        registerAll()
    }

    /** Registers every CalendarIT account with the distributor (again: harmless, and it renews endpoints). */
    suspend fun registerAll() = withContext(Dispatchers.IO) {
        if (prefs.turnedOff) return@withContext
        for (account in db.accounts().all()) {
            if (account.kind != AccountKind.CALENDARIT || account.needsSignIn || account.certificateChanged) continue
            try {
                val vapid = accounts.auth(account).authorized { server, token -> accounts.api(account.certificatePin).vapidPublicKey(server, token) }
                UnifiedPush.register(context, account.id, "24seven · ${account.username}", vapid)
            } catch (e: Exception) {
                Log.w(TAG, "Couldn't register for instant updates", e)
            }
        }
    }

    /** The distributor gave [accountId] an endpoint (or a new one): its server learns where to push. */
    suspend fun endpointFor(accountId: String, push: org.hyperstarit.twentyfourseven.net.DevicePushInput?) {
        prefs.setEndpoint(accountId, push)
        val account = db.accounts().get(accountId) ?: return
        runCatching { accounts.registerDevice(account) }.onFailure { Log.w(TAG, "Couldn't tell the server the push endpoint", it) }
    }

    /** Turns instant updates off: every account leaves the distributor (its server is told by [SyncPushService.onUnregistered]). */
    suspend fun unregisterAll() = withContext(Dispatchers.IO) {
        prefs.turnedOff = true
        for (account in db.accounts().all()) {
            runCatching { UnifiedPush.unregister(context, account.id) }
            if (prefs.endpoint(account.id) != null) endpointFor(account.id, null)
        }
        runCatching { UnifiedPush.removeDistributor(context) }
    }

    /** An account removed: its endpoint goes too. */
    fun forget(accountId: String) {
        runCatching { UnifiedPush.unregister(context, accountId) }
        prefs.setEndpoint(accountId, null)
    }

    private companion object {
        const val TAG = "24sevenPush"
    }
}
