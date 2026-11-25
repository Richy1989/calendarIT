package org.hyperstarit.planit.auth

import android.util.Base64
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import java.util.UUID
import kotlinx.coroutines.flow.first
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** A signed-in account on one server. Times are epoch seconds. */
@Serializable
data class Session(
    val server: String,
    val email: String,
    val accessToken: String,
    val accessExpiresAt: Long,
    val refreshToken: String,
    val refreshExpiresAt: Long,
)

/** Where the session and this installation's device id are kept. */
interface SessionStore {
    suspend fun load(): Session?

    suspend fun save(session: Session?)

    /** This installation's id on the server (/api/devices), made once. */
    suspend fun deviceId(): String
}

/** [SessionStore] in DataStore, the session sealed by a [SecretBox]. */
class DataStoreSessionStore(
    private val store: DataStore<Preferences>,
    private val box: SecretBox,
    private val json: Json,
) : SessionStore {

    override suspend fun load(): Session? {
        val sealed = store.data.first()[SESSION] ?: return null
        val plain = box.open(Base64.decode(sealed, Base64.NO_WRAP)) ?: return null
        return runCatching { json.decodeFromString<Session>(plain.decodeToString()) }.getOrNull()
    }

    override suspend fun save(session: Session?) {
        store.edit { prefs ->
            if (session == null) {
                prefs.remove(SESSION)
            } else {
                val sealed = box.seal(json.encodeToString(Session.serializer(), session).encodeToByteArray())
                prefs[SESSION] = Base64.encodeToString(sealed, Base64.NO_WRAP)
            }
        }
    }

    override suspend fun deviceId(): String {
        store.data.first()[DEVICE_ID]?.let { return it }
        val id = UUID.randomUUID().toString()
        store.edit { prefs -> if (prefs[DEVICE_ID] == null) prefs[DEVICE_ID] = id }
        return store.data.first()[DEVICE_ID]!!
    }

    private companion object {
        val SESSION = stringPreferencesKey("session")
        val DEVICE_ID = stringPreferencesKey("device_id")
    }
}
