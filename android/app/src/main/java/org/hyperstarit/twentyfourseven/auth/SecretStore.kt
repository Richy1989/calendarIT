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

/** Where each account's tokens and this installation's device id are kept. */
interface SecretStore {
    suspend fun tokens(accountId: String): Tokens?

    suspend fun saveTokens(accountId: String, tokens: Tokens?)

    /** This installation's id on servers (/api/devices), made once. */
    suspend fun deviceId(): String

    /** A CalDAV account's password (CalDAV has no tokens: every request carries it). */
    suspend fun password(accountId: String): String? = null

    suspend fun savePassword(accountId: String, password: String?): Unit = throw UnsupportedOperationException()
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

    override suspend fun password(accountId: String): String? = open(store.data.first()[passwordKey(accountId)])

    override suspend fun savePassword(accountId: String, password: String?) {
        store.edit { prefs ->
            if (password == null) prefs.remove(passwordKey(accountId)) else prefs[passwordKey(accountId)] = seal(password)
        }
    }

    override suspend fun deviceId(): String {
        store.data.first()[DEVICE_ID]?.let { return it }
        val id = UUID.randomUUID().toString()
        store.edit { prefs -> if (prefs[DEVICE_ID] == null) prefs[DEVICE_ID] = id }
        return store.data.first()[DEVICE_ID]!!
    }

    private fun seal(plain: String) = Base64.encodeToString(box.seal(plain.encodeToByteArray()), Base64.NO_WRAP)

    private fun open(sealed: String?): String? =
        sealed?.let { box.open(Base64.decode(it, Base64.NO_WRAP))?.decodeToString() }

    private companion object {
        val DEVICE_ID = stringPreferencesKey("device_id")

        fun tokensKey(accountId: String) = stringPreferencesKey("tokens.$accountId")

        fun passwordKey(accountId: String) = stringPreferencesKey("password.$accountId")
    }
}
