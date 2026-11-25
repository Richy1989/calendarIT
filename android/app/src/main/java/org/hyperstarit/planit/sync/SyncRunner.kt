package org.hyperstarit.planit.sync

import java.io.IOException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.hyperstarit.planit.accounts.Accounts
import org.hyperstarit.planit.auth.SessionEndedException
import org.hyperstarit.planit.data.db.AccountKind
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.net.isCertificateChange
import org.hyperstarit.planit.net.isCertificateRefusal

/**
 * Syncs every account, one sync at a time. An account that needs the user — signed out by its
 * server, or a certificate that changed — is left alone until they have acted: asking again
 * couldn't help, and a server counting failures could lock the account.
 */
class SyncRunner(
    private val db: PlanItDatabase,
    private val accounts: Accounts,
    private val planIt: PlanItSync,
    /** What changed may move reminders: they are set again after every sync. */
    private val afterSync: suspend () -> Unit = {},
) {
    private val mutex = Mutex()
    private var devicesRegistered = false

    /** True when every account that could sync did; false when one should be tried again later. */
    suspend fun syncAll(): Boolean = mutex.withLock {
        var allDone = true
        for (account in db.accounts().all()) {
            if (account.needsSignIn || account.certificateChanged || account.kind != AccountKind.PLANIT) continue
            try {
                planIt.sync(account)
                if (!devicesRegistered) runCatching { accounts.registerDevice(account) }
            } catch (_: SessionEndedException) {
                // Accounts marked it as needing sign-in.
            } catch (e: ProtocolException) {
                db.accounts().setError(account.id, e.message)
            } catch (e: IOException) {
                when {
                    e.isCertificateChange() ->
                        db.accounts().setCertificateChanged(account.id, "The server's certificate has changed. Check it before syncing again.")
                    e.isCertificateRefusal() -> {
                        db.accounts().setError(account.id, "The server's certificate isn't trusted.")
                        allDone = false
                    }
                    else -> {
                        db.accounts().setError(account.id, e.message?.takeIf { it.isNotBlank() } ?: "Couldn't reach the server.")
                        allDone = false
                    }
                }
            }
        }
        devicesRegistered = true
        runCatching { afterSync() }
        allDone
    }
}
