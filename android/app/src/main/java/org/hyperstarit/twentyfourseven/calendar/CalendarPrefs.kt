package org.hyperstarit.twentyfourseven.calendar

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/** How this device shows calendars: which are hidden, and the view last used. Device-only. */
class CalendarPrefs(private val store: DataStore<Preferences>) {
    val hidden: Flow<Set<String>> = store.data.map { it[HIDDEN] ?: emptySet() }
    val view: Flow<String?> = store.data.map { it[VIEW] }

    suspend fun setHidden(calendarId: String, hidden: Boolean) {
        store.edit { prefs ->
            val now = prefs[HIDDEN] ?: emptySet()
            prefs[HIDDEN] = if (hidden) now + calendarId else now - calendarId
        }
    }

    suspend fun setView(view: String) {
        store.edit { it[VIEW] = view }
    }

    private companion object {
        val HIDDEN = stringSetPreferencesKey("hidden_calendars")
        val VIEW = stringPreferencesKey("view")
    }
}
