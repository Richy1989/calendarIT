package org.hyperstarit.twentyfourseven.accounts

import androidx.room.withTransaction
import java.io.IOException
import java.time.Clock
import java.util.UUID
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import org.hyperstarit.twentyfourseven.auth.AccountAuth
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.caldav.DavAuthException
import org.hyperstarit.twentyfourseven.caldav.DavClient
import org.hyperstarit.twentyfourseven.data.db.DavAccountEntity
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AccountKind
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.net.ApiClient
import org.hyperstarit.twentyfourseven.net.isCertificateChange
import org.hyperstarit.twentyfourseven.net.isCertificateRefusal
import org.hyperstarit.twentyfourseven.net.CertificateInfo
import org.hyperstarit.twentyfourseven.net.Certificates
import org.hyperstarit.twentyfourseven.net.RegisterDeviceRequest

/** The server's certificate isn't vouched for by any authority: the user decides whether to trust it. */
class UntrustedCertificateException(val certificate: CertificateInfo, val changed: Boolean) :
    IOException(if (changed) "The server's certificate has changed." else "The server's certificate isn't trusted.")

/** This account is already on the device. */
class DuplicateAccountException : IOException("This account is already added.")

/** What a device says about itself when it registers with a server. */
interface DeviceInfo {
    val name: String
    val appVersion: String
}

/**
 * The accounts on this device and everything that speaks for them: each one's network client
 * (with its certificate pin) and its tokens.
 */
class Accounts(
    private val db: AppDatabase,
    private val secrets: SecretStore,
    private val baseHttp: OkHttpClient,
    private val json: Json,
    private val clock: Clock,
    private val device: DeviceInfo,
    /** The account's UnifiedPush endpoint, if it has one (sent with every device registration). */
    private val pushFor: (String) -> org.hyperstarit.twentyfourseven.net.DevicePushInput? = { null },
) {
    val all: Flow<List<AccountEntity>> = db.accounts().observeAll()

    private val lock = Mutex()
    private val auths = mutableMapOf<String, AccountAuth>()

    /** What each account's server was last told about this device (its push target), this process. */
    private val registered = java.util.concurrent.ConcurrentHashMap<String, Registration>()

    private data class Registration(val push: org.hyperstarit.twentyfourseven.net.DevicePushInput?)

    fun api(pin: String?): ApiClient = ApiClient(Certificates.clientFor(baseHttp, pin), json)

    /** The account's session, made once per account (and again when its pin changes). */
    suspend fun auth(account: AccountEntity): AccountAuth = lock.withLock {
        val key = "${account.id}|${account.certificatePin}"
        auths[key] ?: AccountAuth(
            accountId = account.id,
            server = account.server.toHttpUrl(),
            api = api(account.certificatePin),
            secrets = secrets,
            clock = clock,
            onEnded = { markSignedOut(account.id) },
        ).also { auths[key] = it }
    }

    /**
     * Signs in to a CalendarIT server and adds the account. Without [pin], a certificate no authority
     * vouches for stops this with [UntrustedCertificateException] before the password is sent;
     * call again with the pin of the certificate the user chose to trust.
     */
    suspend fun addCalendarIt(server: HttpUrl, email: String, password: String, pin: String?): AccountEntity {
        val username = email.trim()
        if (db.accounts().find(AccountKind.CALENDARIT, server.toString(), username) != null) throw DuplicateAccountException()
        val api = api(pin)
        val tokens = withCertificateCheck(server, pin) { api.login(server, username, password) }
        val account = AccountEntity(
            id = UUID.randomUUID().toString(),
            kind = AccountKind.CALENDARIT,
            server = server.toString(),
            username = username,
            certificatePin = pin,
            createdAt = clock.millis(),
        )
        secrets.saveTokens(account.id, AccountAuth.tokensOf(tokens))
        db.accounts().insert(account)
        runCatching { registerDevice(account) } // the account works without it; the next start tries again
        return account
    }

    fun dav(server: HttpUrl, username: String, password: String, pin: String?): DavClient =
        DavClient(Certificates.clientFor(baseHttp, pin), server.host, username, password)

    /** The account's CalDAV client, with its stored password. */
    suspend fun dav(account: AccountEntity): DavClient {
        val password = secrets.password(account.id) ?: run {
            markSignedOut(account.id, "The password is missing. Sign in again.")
            throw DavAuthException()
        }
        return dav(account.server.toHttpUrl(), account.username, password, account.certificatePin)
    }

    /**
     * Adds a CalDAV account: finds the user's calendars from [server] (the server's address, its
     * /.well-known/caldav, or a calendar's own address). Certificates are handled as for
     * [addCalendarIt]: nothing is sent to a server whose certificate the user hasn't accepted.
     */
    suspend fun addCalDav(server: HttpUrl, username: String, password: String, pin: String?): AccountEntity {
        val user = username.trim()
        if (db.accounts().find(AccountKind.CALDAV, server.toString(), user) != null) throw DuplicateAccountException()
        val found = withCertificateCheck(server, pin) { dav(server, user, password, pin).discover(server) }
        val account = AccountEntity(
            id = UUID.randomUUID().toString(),
            kind = AccountKind.CALDAV,
            server = server.toString(),
            username = user,
            certificatePin = pin,
            createdAt = clock.millis(),
        )
        secrets.savePassword(account.id, password)
        db.withTransaction {
            db.accounts().insert(account)
            db.dav().putAccount(DavAccountEntity(account.id, found.principal?.toString(), found.home.toString(), found.addresses.joinToString("\n")))
        }
        return account
    }

    /** After the server ended the session (or refused the password): signed in again, the account's data kept. */
    suspend fun signInAgain(accountId: String, password: String) {
        val account = db.accounts().get(accountId) ?: return
        if (account.kind == AccountKind.CALDAV) {
            val server = account.server.toHttpUrl()
            val found = withCertificateCheck(server, account.certificatePin) {
                dav(server, account.username, password, account.certificatePin).discover(server)
            }
            secrets.savePassword(account.id, password)
            db.withTransaction {
                db.dav().putAccount(DavAccountEntity(account.id, found.principal?.toString(), found.home.toString(), found.addresses.joinToString("\n")))
                db.accounts().update(account.copy(needsSignIn = false, lastError = null))
            }
            return
        }
        val server = account.server.toHttpUrl()
        val tokens = withCertificateCheck(server, account.certificatePin) {
            api(account.certificatePin).login(server, account.username, password)
        }
        secrets.saveTokens(account.id, AccountAuth.tokensOf(tokens))
        forget(account.id)
        registered.remove(account.id) // a new session: tell the server about this device again
        db.accounts().update(account.copy(needsSignIn = false, lastError = null))
    }

    /**
     * Changes the account's password. The server ends every session on a change, this one too, so
     * the app signs in again with the new password and keeps syncing; when that fails, the account
     * asks for a sign-in like any ended session.
     */
    suspend fun changePassword(account: AccountEntity, current: String, new: String) {
        val auth = auth(account)
        val api = api(account.certificatePin)
        auth.authorized { server, token -> api.changePassword(server, token, current, new) }
        val tokens = try {
            api.login(auth.server, account.username, new)
        } catch (e: IOException) {
            markSignedOut(account.id, "Your password was changed. Sign in again.")
            throw e
        }
        auth.replaceTokens(AccountAuth.tokensOf(tokens))
        registered.remove(account.id)
        db.accounts().get(account.id)?.let { db.accounts().update(it.copy(needsSignIn = false, lastError = null)) }
    }

    /**
     * Asks a CalendarIT server to mail a reset link to [email] (the link opens the web app). The
     * certificate is checked as for [addCalendarIt]: nothing is sent to a server the user hasn't accepted.
     */
    suspend fun requestPasswordReset(server: HttpUrl, email: String, pin: String?) {
        withCertificateCheck(server, pin) { api(pin).forgotPassword(server, email.trim()) }
    }

    /** The user looked at a changed certificate and trusts the new one. */
    suspend fun trustCertificate(accountId: String, sha256: String) {
        val account = db.accounts().get(accountId) ?: return
        forget(account.id)
        db.accounts().update(account.copy(certificatePin = sha256, certificateChanged = false, lastError = null))
    }

    /** The certificate the account's server presents now, for the user to look at. */
    suspend fun presentedCertificate(accountId: String): CertificateInfo? {
        val account = db.accounts().get(accountId) ?: return null
        return Certificates.probe(account.server.toHttpUrl())
    }

    /** Signs out on the server (when reachable) and removes the account and all of its data. */
    suspend fun remove(accountId: String) {
        val account = db.accounts().get(accountId) ?: return
        if (account.kind == AccountKind.CALENDARIT) {
            val auth = auth(account)
            runCatching {
                auth.refreshToken()?.let { api(account.certificatePin).logout(auth.server, it) }
            }
        }
        secrets.saveTokens(accountId, null)
        if (account.kind == AccountKind.CALDAV) secrets.savePassword(accountId, null)
        forget(accountId)
        db.withTransaction {
            db.dav().deleteAccountResources(accountId)
            db.dav().deleteAccountCalendars(accountId)
            db.dav().deleteAccount(accountId)
            db.accountData().deleteAll(
                accountId, db.accounts(), db.calendars(), db.categories(), db.events(), db.tasks(), db.outbox(), db.notices(),
            )
        }
    }

    suspend fun registerDevice(account: AccountEntity) {
        val push = pushFor(account.id)
        val request = RegisterDeviceRequest(name = device.name, platform = "android", appVersion = device.appVersion, push = push)
        val id = secrets.deviceId()
        auth(account).authorized { server, token -> api(account.certificatePin).registerDevice(server, token, id, request) }
        registered[account.id] = Registration(push)
    }

    /**
     * Registers the device with [account]'s server unless it already knows this device and its
     * current push target — so a registration that failed (offline, a server hiccup, a session
     * that had ended) or a push endpoint that changed since is tried again at the next sync,
     * per account.
     */
    /** Tells the server where to push again (it may have forgotten), then has it send a test push. */
    suspend fun testPush(account: AccountEntity): org.hyperstarit.twentyfourseven.net.TestPushDto {
        registerDevice(account)
        val id = secrets.deviceId()
        return auth(account).authorized { server, token -> api(account.certificatePin).testPush(server, token, id) }
    }

    suspend fun registerDeviceIfNeeded(account: AccountEntity) {
        if (registered[account.id] == Registration(pushFor(account.id))) return
        registerDevice(account)
    }

    internal suspend fun markSignedOut(accountId: String, message: String = "Signed out by the server. Sign in again.") {
        db.accounts().get(accountId)?.let {
            db.accounts().update(it.copy(needsSignIn = true, lastError = message))
        }
    }

    private suspend fun forget(accountId: String) = lock.withLock {
        auths.keys.removeAll { it.startsWith("$accountId|") }
    }

    /**
     * Runs [call]; when TLS refuses the server's certificate, says which certificate it was, so the
     * user can decide. Nothing of [call] reached the server in that case: the handshake failed first.
     */
    private suspend fun <T> withCertificateCheck(server: HttpUrl, pin: String?, call: suspend () -> T): T {
        try {
            return call()
        } catch (e: IOException) {
            if (server.scheme != "https" || e is UntrustedCertificateException) throw e
            val changed = e.isCertificateChange()
            if (!changed && !(pin == null && e.isCertificateRefusal())) throw e
            val info = runCatching { Certificates.probe(server) }.getOrNull() ?: throw e
            throw UntrustedCertificateException(info, changed)
        }
    }
}
