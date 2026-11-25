package org.hyperstarit.twentyfourseven.system

import android.accounts.AbstractAccountAuthenticator
import android.accounts.Account
import android.accounts.AccountAuthenticatorResponse
import android.accounts.AccountManager
import android.app.Service
import android.content.AbstractThreadedSyncAdapter
import android.content.ContentProviderClient
import android.content.Context
import android.content.Intent
import android.content.SyncResult
import android.os.Bundle
import android.os.IBinder
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.runBlocking
import org.hyperstarit.twentyfourseven.MainActivity
import org.hyperstarit.twentyfourseven.sync.SyncScheduler

/**
 * 24seven's account type, as Android needs one for a calendar. Accounts are only made by the app;
 * "add account" in the system settings opens the app instead.
 */
class Authenticator(private val context: Context) : AbstractAccountAuthenticator(context) {
    override fun addAccount(
        response: AccountAuthenticatorResponse?, accountType: String?, authTokenType: String?, requiredFeatures: Array<out String>?, options: Bundle?,
    ): Bundle = Bundle().apply { putParcelable(AccountManager.KEY_INTENT, Intent(context, MainActivity::class.java)) }

    override fun editProperties(response: AccountAuthenticatorResponse?, accountType: String?): Bundle? = null
    override fun confirmCredentials(response: AccountAuthenticatorResponse?, account: Account?, options: Bundle?): Bundle? = null
    override fun getAuthToken(response: AccountAuthenticatorResponse?, account: Account?, authTokenType: String?, options: Bundle?): Bundle? = null
    override fun getAuthTokenLabel(authTokenType: String?): String? = null
    override fun updateCredentials(response: AccountAuthenticatorResponse?, account: Account?, authTokenType: String?, options: Bundle?): Bundle? = null
    override fun hasFeatures(response: AccountAuthenticatorResponse?, account: Account?, features: Array<out String>?): Bundle? = null
}

class AuthenticatorService : Service() {
    private lateinit var authenticator: Authenticator

    override fun onCreate() {
        authenticator = Authenticator(this)
    }

    override fun onBind(intent: Intent?): IBinder? = authenticator.iBinder
}

/**
 * Android runs this when another app changed 24seven's calendars (or someone pressed "sync" in the
 * system settings): their changes come into 24seven, and go on to the server.
 */
@AndroidEntryPoint
class CalendarSyncService : Service() {
    @Inject lateinit var system: SystemCalendar
    @Inject lateinit var scheduler: SyncScheduler
    private lateinit var adapter: AbstractThreadedSyncAdapter

    override fun onCreate() {
        super.onCreate()
        adapter = object : AbstractThreadedSyncAdapter(this, true, false) {
            override fun onPerformSync(account: Account, extras: Bundle, authority: String, provider: ContentProviderClient, syncResult: SyncResult) {
                runBlocking { system.sync() } // the adapter's own thread: it waits for the result
                scheduler.requestNow()
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = adapter.syncAdapterBinder
}
