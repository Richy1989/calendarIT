package org.hyperstarit.planit.auth

import java.io.IOException
import java.time.Clock
import java.time.OffsetDateTime
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.hyperstarit.planit.net.ApiClient
import org.hyperstarit.planit.net.AuthTokens
import org.hyperstarit.planit.net.UnauthorizedException

sealed interface AuthState {
    data object Loading : AuthState

    /** [reason] says why, when it wasn't the user's doing (a session ended elsewhere). */
    data class SignedOut(val reason: String? = null) : AuthState

    data class SignedIn(val server: String, val email: String) : AuthState
}

/** The session ended: signed out on the server, or the refresh token expired. Sign in again. */
class SessionEndedException : IOException("You were signed out. Sign in again.")

/**
 * Owns the session: signing in and out, and handing out a valid access token.
 *
 * Refresh tokens rotate, so two refreshes at once would make the server see the same token twice
 * (it forgives that within 30 s, but no more): every refresh goes through one [Mutex], and a
 * caller that waited behind another's refresh takes its result instead of refreshing again.
 *
 * It never signs in on its own. A login the server refuses is the user's to retry: repeating it
 * would walk the account into the server's lockout (10 failures in 15 minutes). A refused
 * refresh ends the session here too, so nothing keeps knocking with a dead token.
 */
class SessionManager(
    private val api: ApiClient,
    private val store: SessionStore,
    private val clock: Clock,
    scope: CoroutineScope,
    private val onSignedIn: suspend (SessionManager) -> Unit = {},
    private val onSignedOut: suspend () -> Unit = {},
) {
    private val mutex = Mutex()
    private var session: Session? = null
    private val _state = MutableStateFlow<AuthState>(AuthState.Loading)
    val state: StateFlow<AuthState> = _state.asStateFlow()

    private val loaded = scope.launch {
        val restored = store.load()
        mutex.withLock { session = restored }
        _state.value = restored?.let { AuthState.SignedIn(it.server, it.email) } ?: AuthState.SignedOut()
    }

    /** Signs in; throws [UnauthorizedException] (with the server's words) or an [IOException]. */
    suspend fun login(server: HttpUrl, email: String, password: String) {
        loaded.join()
        val tokens = api.login(server, email.trim(), password)
        val signedIn = sessionOf(server.toString(), email.trim(), tokens)
        mutex.withLock { session = signedIn }
        store.save(signedIn)
        _state.value = AuthState.SignedIn(signedIn.server, signedIn.email)
        runCatching { onSignedIn(this) } // e.g. device registration: never fails the sign-in
    }

    /** Ends the session here and, when reachable, on the server. */
    suspend fun logout() {
        loaded.join()
        val ending = mutex.withLock { session.also { session = null } }
        store.save(null)
        _state.value = AuthState.SignedOut()
        onSignedOut()
        if (ending != null) {
            runCatching { api.logout(ending.server.toHttpUrl(), ending.refreshToken) }
        }
    }

    /**
     * Calls [block] with the server and a valid access token. A 401 gets one refresh and one
     * retry (the token may have been revoked early); a second 401 ends the session.
     */
    suspend fun <T> authorized(block: suspend (server: HttpUrl, token: String) -> T): T {
        loaded.join()
        val (server, token) = accessToken(force = false, stale = null)
        return try {
            block(server, token)
        } catch (_: UnauthorizedException) {
            val (again, fresh) = accessToken(force = true, stale = token)
            try {
                block(again, fresh)
            } catch (_: UnauthorizedException) {
                end()
                throw SessionEndedException()
            }
        }
    }

    private suspend fun accessToken(force: Boolean, stale: String?): Pair<HttpUrl, String> = mutex.withLock {
        val current = session ?: throw SessionEndedException()
        val now = clock.instant().epochSecond
        val server = current.server.toHttpUrl()
        val stillGood = current.accessExpiresAt - now > EARLY_REFRESH_SECONDS
        // Someone else refreshed while this caller waited: their token is the one to use.
        if ((!force && stillGood) || (force && stale != null && current.accessToken != stale)) {
            return@withLock server to current.accessToken
        }
        if (current.refreshExpiresAt <= now) {
            endLocked()
            throw SessionEndedException()
        }
        val tokens = try {
            api.refresh(server, current.refreshToken)
        } catch (_: UnauthorizedException) {
            endLocked()
            throw SessionEndedException()
        }
        val renewed = sessionOf(current.server, current.email, tokens)
        session = renewed
        store.save(renewed) // the old refresh token is spent: the new one must survive a restart
        server to renewed.accessToken
    }

    private suspend fun end() = mutex.withLock { endLocked() }

    private suspend fun endLocked() {
        if (session == null) return
        session = null
        store.save(null)
        _state.value = AuthState.SignedOut(reason = "You were signed out. Sign in again.")
        onSignedOut()
    }

    private fun sessionOf(server: String, email: String, tokens: AuthTokens) = Session(
        server = server,
        email = email,
        accessToken = tokens.accessToken,
        accessExpiresAt = epoch(tokens.accessTokenExpiresAt),
        refreshToken = tokens.refreshToken,
        refreshExpiresAt = epoch(tokens.refreshTokenExpiresAt),
    )

    private fun epoch(iso: String) = OffsetDateTime.parse(iso).toEpochSecond()

    private companion object {
        /** Refresh this long before the access token runs out, like the web app. */
        const val EARLY_REFRESH_SECONDS = 30L
    }
}
