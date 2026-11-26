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
    val hiddenCategories = store.data.map { it[HIDDEN_CATEGORIES].orEmpty() }
    val view: Flow<String?> = store.data.map { it[VIEW] }

    /** Expand the old calendar-id preferences to account-specific keys on the first edit. */
    suspend fun updateVisibility(category: Boolean, key: String, keys: Set<String>, only: Boolean) {
        val pref = if (category) HIDDEN_CATEGORIES else HIDDEN
        store.edit { data ->
            val saved = data[pref].orEmpty()
            val hidden = keys.filterTo(mutableSetOf()) { it in saved || (!category && it.substringAfter('\u0000') in saved) }
            data[pref] = when {
                only -> keys - key
                key in hidden -> hidden - key
                else -> hidden + key
            }
        }
    }

    suspend fun setView(view: String) {
        store.edit { it[VIEW] = view }
    }

    private companion object {
        val HIDDEN = stringSetPreferencesKey("hidden_calendars")
        val HIDDEN_CATEGORIES = stringSetPreferencesKey("hidden_categories")
        val VIEW = stringPreferencesKey("view")
    }
}
