package org.hyperstarit.twentyfourseven.sync

import java.io.IOException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.auth.SessionEndedException
import org.hyperstarit.twentyfourseven.data.db.AccountKind
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.net.isCertificateChange
import org.hyperstarit.twentyfourseven.net.isCertificateRefusal

/**
 * Syncs every account, one sync at a time. An account that needs the user — signed out by its
 * server, or a certificate that changed — is left alone until they have acted: asking again
 * couldn't help, and a server counting failures could lock the account.
 */
class SyncRunner(
    private val db: AppDatabase,
    private val accounts: Accounts,
    private val calendarIt: CalendarItSync,
    /** What changed may move reminders: they are set again after every sync. */
    private val afterSync: suspend () -> Unit = {},
) {
    private val mutex = Mutex()

    /** True when every account that could sync did; false when one should be tried again later. */
    suspend fun syncAll(): Boolean = mutex.withLock {
        var allDone = true
        for (account in db.accounts().all()) {
            if (account.needsSignIn || account.certificateChanged || account.kind != AccountKind.CALENDARIT) continue
            try {
                calendarIt.sync(account)
                // Not needed for syncing; one that fails is tried again at the next sync.
                runCatching { accounts.registerDeviceIfNeeded(account) }
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
        runCatching { afterSync() }
        allDone
    }
}
