package org.hyperstarit.twentyfourseven.auth

import android.util.Base64
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import java.util.UUID
import kotlinx.coroutines.flow.first
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** An account's session with its server. Times are epoch seconds. */
@Serializable
data class Tokens(
    val accessToken: String,
    val accessExpiresAt: Long,
    val refreshToken: String,
    val refreshExpiresAt: Long,
)

/** The single session phase 1 kept, before there were accounts. */
@Serializable
data class LegacySession(
    val server: String,
    val email: String,
    val accessToken: String,
    val accessExpiresAt: Long,
    val refreshToken: String,
    val refreshExpiresAt: Long,
)

/** Where each account's tokens and this installation's device id are kept. */
interface SecretStore {
    suspend fun tokens(accountId: String): Tokens?

    suspend fun saveTokens(accountId: String, tokens: Tokens?)

    /** This installation's id on servers (/api/devices), made once. */
    suspend fun deviceId(): String

    /** Phase 1's session, handed over once to become an account. */
    suspend fun takeLegacySession(): LegacySession?
}

/** [SecretStore] in DataStore, every secret sealed by a [SecretBox]. */
class DataStoreSecretStore(
    private val store: DataStore<Preferences>,
    private val box: SecretBox,
    private val json: Json,
) : SecretStore {

    override suspend fun tokens(accountId: String): Tokens? =
        open(store.data.first()[tokensKey(accountId)])?.let { runCatching { json.decodeFromString<Tokens>(it) }.getOrNull() }

    override suspend fun saveTokens(accountId: String, tokens: Tokens?) {
        store.edit { prefs ->
            if (tokens == null) prefs.remove(tokensKey(accountId))
            else prefs[tokensKey(accountId)] = seal(json.encodeToString(Tokens.serializer(), tokens))
        }
    }

    override suspend fun deviceId(): String {
        store.data.first()[DEVICE_ID]?.let { return it }
        val id = UUID.randomUUID().toString()
        store.edit { prefs -> if (prefs[DEVICE_ID] == null) prefs[DEVICE_ID] = id }
        return store.data.first()[DEVICE_ID]!!
    }

    override suspend fun takeLegacySession(): LegacySession? {
        val sealed = store.data.first()[LEGACY_SESSION] ?: return null
        store.edit { it.remove(LEGACY_SESSION) }
        return open(sealed)?.let { runCatching { json.decodeFromString<LegacySession>(it) }.getOrNull() }
    }

    private fun seal(plain: String) = Base64.encodeToString(box.seal(plain.encodeToByteArray()), Base64.NO_WRAP)

    private fun open(sealed: String?): String? =
        sealed?.let { box.open(Base64.decode(it, Base64.NO_WRAP))?.decodeToString() }

    private companion object {
        val LEGACY_SESSION = stringPreferencesKey("session")
        val DEVICE_ID = stringPreferencesKey("device_id")

        fun tokensKey(accountId: String) = stringPreferencesKey("tokens.$accountId")
    }
}
