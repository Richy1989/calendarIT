package org.hyperstarit.planit.auth

import java.io.IOException
import java.time.Clock
import java.time.OffsetDateTime
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.HttpUrl
import org.hyperstarit.planit.net.ApiClient
import org.hyperstarit.planit.net.AuthTokens
import org.hyperstarit.planit.net.UnauthorizedException

/** The session ended: signed out on the server, or the refresh token expired. Sign in again. */
class SessionEndedException : IOException("Signed out by the server. Sign in again.")

/**
 * One PlanIT account's tokens, handing out a valid access token.
 *
 * Refresh tokens rotate, so two refreshes at once would make the server see the same token twice
 * (it forgives that within 30 s, but no more): every refresh goes through one [Mutex], and a
 * caller that waited behind another's refresh takes its result instead of refreshing again.
 *
 * It never signs in on its own. A refused refresh ends the session ([onEnded]): nothing then
 * knocks with a dead token, and no retried password walks the account into the server's lockout.
 */
class AccountAuth(
    val accountId: String,
    val server: HttpUrl,
    private val api: ApiClient,
    private val secrets: SecretStore,
    private val clock: Clock,
    private val onEnded: suspend () -> Unit,
) {
    private val mutex = Mutex()
    private var tokens: Tokens? = null
    private var loaded = false

    /**
     * Calls [block] with a valid access token. A 401 gets one refresh and one retry (the token may
     * have been revoked early); a second 401 ends the session.
     */
    suspend fun <T> authorized(block: suspend (server: HttpUrl, token: String) -> T): T {
        val token = accessToken(force = false, stale = null)
        return try {
            block(server, token)
        } catch (_: UnauthorizedException) {
            val fresh = accessToken(force = true, stale = token)
            try {
                block(server, fresh)
            } catch (_: UnauthorizedException) {
                mutex.withLock { endLocked() }
                throw SessionEndedException()
            }
        }
    }

    /** The server's word on whether we are still signed in, for signing out. */
    suspend fun refreshToken(): String? = mutex.withLock { load()?.refreshToken }

    private suspend fun load(): Tokens? {
        if (!loaded) {
            tokens = secrets.tokens(accountId)
            loaded = true
        }
        return tokens
    }

    private suspend fun accessToken(force: Boolean, stale: String?): String = mutex.withLock {
        val current = load() ?: throw SessionEndedException()
        val now = clock.instant().epochSecond
        val stillGood = current.accessExpiresAt - now > EARLY_REFRESH_SECONDS
        // Someone else refreshed while this caller waited: their token is the one to use.
        if ((!force && stillGood) || (force && stale != null && current.accessToken != stale)) {
            return@withLock current.accessToken
        }
        if (current.refreshExpiresAt <= now) {
            endLocked()
            throw SessionEndedException()
        }
        val renewed = try {
            tokensOf(api.refresh(server, current.refreshToken))
        } catch (_: UnauthorizedException) {
            endLocked()
            throw SessionEndedException()
        }
        tokens = renewed
        secrets.saveTokens(accountId, renewed) // the old refresh token is spent: the new one must survive a restart
        renewed.accessToken
    }

    private suspend fun endLocked() {
        if (tokens == null && loaded) return
        tokens = null
        loaded = true
        secrets.saveTokens(accountId, null)
        onEnded()
    }

    companion object {
        /** Refresh this long before the access token runs out, like the web app. */
        const val EARLY_REFRESH_SECONDS = 30L

        fun tokensOf(t: AuthTokens) = Tokens(
            accessToken = t.accessToken,
            accessExpiresAt = OffsetDateTime.parse(t.accessTokenExpiresAt).toEpochSecond(),
            refreshToken = t.refreshToken,
            refreshExpiresAt = OffsetDateTime.parse(t.refreshTokenExpiresAt).toEpochSecond(),
        )
    }
}
