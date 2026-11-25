package org.hyperstarit.planit.push

import android.content.Context
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.data.db.AccountKind
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.unifiedpush.android.connector.UnifiedPush

/**
 * Instant updates through UnifiedPush — no Google services: the user's distributor app (e.g. ntfy)
 * holds the connection, each PlanIT server pushes a content-free "sync" to it, and the app syncs.
 * Without a distributor nothing changes: the app keeps syncing on its schedule and when opened.
 */
class PushRegistrar(
    private val context: Context,
    private val db: PlanItDatabase,
    private val accounts: Accounts,
    private val prefs: PushPrefs,
) {
    /** The distributor apps installed (package names). */
    fun distributors(): List<String> = UnifiedPush.getDistributors(context)

    /** The distributor in use, or null. */
    fun current(): String? = runCatching { UnifiedPush.resolveDefaultDistributor(context) }.getOrNull()?.let { r ->
        (r as? org.unifiedpush.android.connector.data.ResolvedDistributor.Found)?.packageName
    }

    val active: Boolean get() = prefs.hasAny()

    /** Registers every PlanIT account with the distributor (again: harmless, and it renews endpoints). */
    suspend fun registerAll() = withContext(Dispatchers.IO) {
        for (account in db.accounts().all()) {
            if (account.kind != AccountKind.PLANIT || account.needsSignIn || account.certificateChanged) continue
            try {
                val vapid = accounts.auth(account).authorized { server, token -> accounts.api(account.certificatePin).vapidPublicKey(server, token) }
                UnifiedPush.register(context, account.id, "PlanIt · ${account.username}", vapid)
            } catch (e: Exception) {
                Log.w(TAG, "Couldn't register for instant updates", e)
            }
        }
    }

    /** The distributor gave [accountId] an endpoint (or a new one): its server learns where to push. */
    suspend fun endpointFor(accountId: String, push: org.hyperstarit.planit.net.DevicePushInput?) {
        prefs.setEndpoint(accountId, push)
        val account = db.accounts().get(accountId) ?: return
        runCatching { accounts.registerDevice(account) }.onFailure { Log.w(TAG, "Couldn't tell the server the push endpoint", it) }
    }

    /** An account removed: its endpoint goes too. */
    fun forget(accountId: String) {
        runCatching { UnifiedPush.unregister(context, accountId) }
        prefs.setEndpoint(accountId, null)
    }

    private companion object {
        const val TAG = "PlanItPush"
    }
}
