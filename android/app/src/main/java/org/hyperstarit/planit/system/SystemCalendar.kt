package org.hyperstarit.planit.system

import android.Manifest
import android.accounts.Account
import android.accounts.AccountManager
import android.content.ContentProviderOperation
import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.database.Cursor
import android.net.Uri
import android.os.Bundle
import android.provider.CalendarContract
import android.provider.CalendarContract.Calendars
import android.provider.CalendarContract.Events
import android.util.Log
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import androidx.core.graphics.toColorInt
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import org.hyperstarit.planit.R
import org.hyperstarit.planit.data.db.AccountEntity
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.CategoryEntity
import org.hyperstarit.planit.data.db.EventData
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.sync.LocalChanges

/**
 * Shows PlanIt's appointments in Android's own calendar, where other calendar apps see and edit
 * them. Each PlanIt account is an Android account of PlanIt's type, each calendar a calendar of
 * it, written as their sync adapter. The phone's PlanIt data stays the truth: a run first takes
 * in what other apps changed (as PlanIt changes, queued for the server like any other), then
 * writes PlanIt's state over the system calendar. Tasks have no place there and stay in PlanIt.
 */
class SystemCalendar(
    private val context: Context,
    private val db: PlanItDatabase,
    private val changes: LocalChanges,
) {
    private val mutex = Mutex()
    private val prefs = context.getSharedPreferences("system_calendar", Context.MODE_PRIVATE)
    private val resolver: ContentResolver = context.contentResolver
    private val accounts = AccountManager.get(context)
    val accountType: String = context.getString(R.string.account_type)

    var enabled: Boolean
        get() = prefs.getBoolean("enabled", false)
        set(value) = prefs.edit { putBoolean("enabled", value) }

    fun permitted(): Boolean = listOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR)
        .all { ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED }

    /** Brings the system calendar in step; does nothing while it's off or not permitted. */
    suspend fun sync() = mutex.withLock {
        withContext(Dispatchers.IO) {
            if (!enabled || !permitted()) return@withContext
            try {
                for ((account, android) in ensureAccounts()) {
                    importChanges(account, android)
                    export(account, android)
                }
            } catch (e: Exception) {
                // A provider that misbehaves must not take PlanIt's sync down with it.
                Log.w(TAG, "System calendar sync failed", e)
            }
        }
    }

    /** Turned off: the accounts go, and Android removes their calendars with them. */
    suspend fun turnOff() = mutex.withLock {
        enabled = false
        withContext(Dispatchers.IO) { accounts.getAccountsByType(accountType).forEach { accounts.removeAccountExplicitly(it) } }
    }

    // ------------------------------------------------------------------ accounts

    private suspend fun ensureAccounts(): List<Pair<AccountEntity, Account>> {
        val ours = db.accounts().all()
        val existing = accounts.getAccountsByType(accountType).associateBy { accounts.getUserData(it, KEY_ID) }
        existing.filterKeys { id -> ours.none { it.id == id } }.values.forEach { accounts.removeAccountExplicitly(it) }
        return ours.map { a ->
            val android = existing[a.id] ?: Account(nameOf(a), accountType).also {
                accounts.addAccountExplicitly(it, null, Bundle().apply { putString(KEY_ID, a.id) })
            }
            // Every run, not only at creation: an account made by a run that failed half-way gets them too.
            if (ContentResolver.getIsSyncable(android, CalendarContract.AUTHORITY) != 1) {
                ContentResolver.setIsSyncable(android, CalendarContract.AUTHORITY, 1)
            }
            if (!ContentResolver.getSyncAutomatically(android, CalendarContract.AUTHORITY)) {
                ContentResolver.setSyncAutomatically(android, CalendarContract.AUTHORITY, true)
            }
            a to android
        }
    }

    /** "me@example.com (cal.example.com)": unique, and what the user sees in other apps. */
    private fun nameOf(a: AccountEntity) = "${a.username} (${a.server.toHttpUrlOrNull()?.host ?: a.server})"

    private fun Uri.asSyncAdapter(account: Account): Uri = buildUpon()
        .appendQueryParameter(CalendarContract.CALLER_IS_SYNCADAPTER, "true")
        .appendQueryParameter(Calendars.ACCOUNT_NAME, account.name)
        .appendQueryParameter(Calendars.ACCOUNT_TYPE, account.type)
        .build()

    /** The system calendars of [android]: their sync id (PlanIt's calendar id) → row id. */
    private fun providerCalendars(android: Account): Map<String, Long> = buildMap {
        resolver.query(
            Calendars.CONTENT_URI.asSyncAdapter(android), arrayOf(Calendars._ID, Calendars._SYNC_ID),
            "${Calendars.ACCOUNT_NAME} = ? AND ${Calendars.ACCOUNT_TYPE} = ?", arrayOf(android.name, android.type), null,
        )?.use { c -> while (c.moveToNext()) c.getString(1)?.let { put(it, c.getLong(0)) } }
    }

    // ------------------------------------------------------------------ in: what other apps changed

    private suspend fun importChanges(account: AccountEntity, android: Account) {
        val calendars = db.calendars().forAccount(account.id).associateBy { it.id }
        val byRow = providerCalendars(android).entries.associate { (sync, id) -> id to sync }
        if (byRow.isEmpty()) return
        val uri = Events.CONTENT_URI.asSyncAdapter(android)
        val rows = query(
            uri, IMPORT_COLUMNS,
            "${Events.CALENDAR_ID} IN (${byRow.keys.joinToString(",")}) AND (${Events.DIRTY} = 1 OR ${Events.DELETED} = 1 OR ${Events._SYNC_ID} IS NULL)",
        )
        for (row in rows) {
            val rowId = (row[Events._ID] as Number).toLong()
            val calendar = calendars[byRow[(row[Events.CALENDAR_ID] as Number).toLong()]] ?: continue
            val deleted = (row[Events.DELETED] as? Number)?.toInt() == 1
            val syncId = row[Events._SYNC_ID] as? String
            val seriesId = row[Events.ORIGINAL_SYNC_ID] as? String
            try {
                when {
                    // A read-only (subscribed) calendar can't be changed: the next export puts it back.
                    calendar.readOnly -> if (syncId == null || deleted) delete(uri, rowId)
                    seriesId != null -> importException(row, seriesId, deleted, syncId, uri, rowId)
                    deleted -> {
                        if (syncId != null) changes.deleteEvent(syncId)
                        delete(uri, rowId)
                    }
                    syncId == null -> {
                        val id = changes.createEvent(newEvent(account, calendar, ProviderMapping.incoming(row)))
                        resolver.update(ContentUris(uri, rowId), ContentValues().apply { put(Events._SYNC_ID, id) }, null, null)
                    }
                    else -> importEdit(syncId, calendar, ProviderMapping.incoming(row), calendars, byRow, row)
                }
            } catch (e: Exception) {
                Log.w(TAG, "Couldn't take in a change from the system calendar", e)
            }
        }
    }

    private suspend fun importException(row: Map<String, Any?>, seriesId: String, deleted: Boolean, syncId: String?, uri: Uri, rowId: Long) {
        val instance = (row[Events.ORIGINAL_INSTANCE_TIME] as? Number)?.toLong() ?: return
        val canceled = (row[Events.STATUS] as? Number)?.toInt() == Events.STATUS_CANCELED
        val series = db.events().live(seriesId)?.data ?: return
        when {
            deleted -> Unit // an edited occurrence taken back: PlanIt's own state is written again
            canceled -> changes.deleteOccurrence(seriesId, instance)
            else -> {
                val inc = ProviderMapping.incoming(row)
                changes.editOccurrence(
                    seriesId, instance,
                    series.copy(
                        title = inc.title, description = inc.description, location = inc.location, start = inc.start, end = inc.end,
                        allDay = inc.allDay, timeZone = inc.timeZone ?: series.timeZone, recurrence = null,
                    ),
                )
            }
        }
        // The row another app made goes; the export writes the occurrence as PlanIt has it now.
        if (syncId == null || deleted) delete(uri, rowId)
    }

    private suspend fun importEdit(
        syncId: String, calendar: CalendarEntity, inc: ProviderMapping.Incoming, calendars: Map<String, CalendarEntity>,
        byRow: Map<Long, String>, row: Map<String, Any?>,
    ) {
        val current = db.events().live(syncId)?.data ?: return
        val overrides = (db.events().overrideEdits(syncId).filter { !it.localDeleted }.map { it.data }
            .ifEmpty { db.events().serverOverrides(syncId).map { it.data } }).map { it.recurrenceId }.toSet()
        // Occurrences deleted in the other app arrive as new EXDATEs.
        if (current.recurrence != null && inc.rule == current.recurrence) {
            val known = current.exDates.toSet()
            inc.exDates.filter { it !in known && it !in overrides }.forEach { changes.deleteOccurrence(syncId, it) }
        }
        val movedTo = byRow[(row[Events.CALENDAR_ID] as Number).toLong()]?.takeIf { it in calendars } ?: calendar.id
        val edited = current.copy(
            calendarId = movedTo, title = inc.title, description = inc.description, location = inc.location,
            start = inc.start, end = inc.end, allDay = inc.allDay, timeZone = inc.timeZone ?: current.timeZone, recurrence = inc.rule,
        )
        val after = db.events().live(syncId)?.data ?: return
        if (edited.copy(exDates = after.exDates, version = after.version) != after) {
            changes.updateEvent(edited.copy(exDates = after.exDates, version = after.version))
        }
    }

    private fun newEvent(account: AccountEntity, calendar: CalendarEntity, inc: ProviderMapping.Incoming) = EventData(
        id = UUID.randomUUID().toString(), accountId = account.id, calendarId = calendar.id, title = inc.title,
        description = inc.description, location = inc.location, categoryId = null, color = null, start = inc.start, end = inc.end,
        allDay = inc.allDay, timeZone = inc.timeZone, recurrence = inc.rule, exDates = emptyList(),
        // Other apps can't set PlanIt reminders here; a new timed one gets its calendar's defaults, as on the web.
        reminders = if (inc.allDay) emptyList() else calendar.defaultReminders, attendees = emptyList(),
        invitationStatus = null, organizerEmail = null, version = null,
    )

    // ------------------------------------------------------------------ out: PlanIt's state

    private suspend fun export(account: AccountEntity, android: Account) {
        val calUri = Calendars.CONTENT_URI.asSyncAdapter(android)
        val calendars = db.calendars().forAccount(account.id)
        val existing = providerCalendars(android)
        for (c in calendars) {
            val values = ContentValues().apply {
                put(Calendars.ACCOUNT_NAME, android.name)
                put(Calendars.ACCOUNT_TYPE, android.type)
                put(Calendars._SYNC_ID, c.id)
                put(Calendars.NAME, c.name)
                put(Calendars.CALENDAR_DISPLAY_NAME, c.name)
                put(Calendars.CALENDAR_COLOR, colorOf(c.color) ?: DEFAULT_COLOR)
                put(Calendars.CALENDAR_ACCESS_LEVEL, if (c.readOnly) Calendars.CAL_ACCESS_READ else Calendars.CAL_ACCESS_OWNER)
                put(Calendars.OWNER_ACCOUNT, account.username)
                put(Calendars.VISIBLE, 1)
                put(Calendars.SYNC_EVENTS, 1)
                c.timeZone?.let { put(Calendars.CALENDAR_TIME_ZONE, it) }
                // Reminders are PlanIt's to ring: other apps offer none here.
                put(Calendars.MAX_REMINDERS, 0)
                put(Calendars.ALLOWED_REMINDERS, "")
            }
            val id = existing[c.id]
            if (id == null) resolver.insert(calUri, values) else resolver.update(ContentUris(calUri, id), values, null, null)
        }
        existing.filterKeys { id -> calendars.none { it.id == id } }.values.forEach { resolver.delete(ContentUris(calUri, it), null, null) }

        val providerIds = providerCalendars(android)
        if (providerIds.isEmpty()) return
        val categories = db.categories().observeAll().first().associateBy { it.id }
        val byId = calendars.associateBy { it.id }
        val events = db.events().observeLive().first().map { it.data }.filter { it.calendarId in providerIds }
        val overridesBySeries = db.events().observeLiveOverrides().first().map { it.data }.groupBy { it.seriesId }

        // What should be there: masters first, so their exceptions find them.
        val desired = LinkedHashMap<String, ContentValues>()
        for (e in events) {
            val overrides = overridesBySeries[e.id].orEmpty()
            val edited = overrides.map { it.recurrenceId }.toSet()
            val color = eventColor(e.categoryId, byId[e.calendarId], categories, e.color)
            desired[e.id] = values(ProviderMapping.eventRow(e, e.exDates.filter { it !in edited }, color), providerIds.getValue(e.calendarId))
        }
        for (e in events.filter { it.recurrence != null }) {
            for (o in overridesBySeries[e.id].orEmpty()) {
                val color = eventColor(o.categoryId, byId[e.calendarId], categories, e.color)
                val row = ProviderMapping.exceptionRow(o, e, color)
                desired[row["_sync_id"] as String] = values(row, providerIds.getValue(e.calendarId))
            }
        }

        val evUri = Events.CONTENT_URI.asSyncAdapter(android)
        val present = query(evUri, arrayOf(Events._ID, Events._SYNC_ID, Events.SYNC_DATA2, Events.DIRTY, Events.CALENDAR_ID),
            "${Events.CALENDAR_ID} IN (${providerIds.values.joinToString(",")})")
            .mapNotNull { r -> (r[Events._SYNC_ID] as? String)?.let { it to r } }.toMap()
        val ops = ArrayList<ContentProviderOperation>()
        for ((syncId, v) in desired) {
            val fingerprint = v.getAsString(Events.SYNC_DATA2)
            val row = present[syncId]
            when {
                row == null -> ops += ContentProviderOperation.newInsert(evUri).withValues(v).build()
                row[Events.SYNC_DATA2] != fingerprint || (row[Events.DIRTY] as? Number)?.toInt() == 1 ||
                    (row[Events.CALENDAR_ID] as Number).toLong() != v.getAsLong(Events.CALENDAR_ID) ->
                    ops += ContentProviderOperation.newUpdate(ContentUris(evUri, (row[Events._ID] as Number).toLong()))
                        .withValues(ContentValues(v).apply { put(Events.DIRTY, 0) }).build()
            }
        }
        present.filterKeys { it !in desired }.values.forEach { row ->
            ops += ContentProviderOperation.newDelete(ContentUris(evUri, (row[Events._ID] as Number).toLong())).build()
        }
        ops.chunked(BATCH).forEach { resolver.applyBatch(CalendarContract.AUTHORITY, ArrayList(it)) }
    }

    private fun values(row: Map<String, Any?>, calendarRowId: Long) = ContentValues().apply {
        put(Events.CALENDAR_ID, calendarRowId)
        for ((k, v) in row) when (v) {
            null -> putNull(k)
            is Int -> put(k, v)
            is Long -> put(k, v)
            else -> put(k, v.toString())
        }
        put(Events.SYNC_DATA2, ProviderMapping.fingerprint(row + ("calendar" to calendarRowId)))
    }

    private fun eventColor(categoryId: String?, calendar: CalendarEntity?, categories: Map<String, CategoryEntity>, own: String?): Int? =
        colorOf(
            categoryId?.let { categories[it]?.color }
                ?: (if (categoryId == null) calendar?.defaultCategoryId?.let { categories[it]?.color } else null)
                ?: own,
        )

    private fun colorOf(hex: String?): Int? = hex?.let { runCatching { it.toColorInt() }.getOrNull() }

    // ------------------------------------------------------------------ provider plumbing

    private fun query(uri: Uri, columns: Array<String>, selection: String): List<Map<String, Any?>> =
        resolver.query(uri, columns, selection, null, null)?.use { c -> generateSequence { if (c.moveToNext()) read(c) else null }.toList() }.orEmpty()

    private fun read(c: Cursor): Map<String, Any?> = (0 until c.columnCount).associate { i ->
        c.getColumnName(i) to when (c.getType(i)) {
            Cursor.FIELD_TYPE_INTEGER -> c.getLong(i)
            Cursor.FIELD_TYPE_FLOAT -> c.getDouble(i)
            Cursor.FIELD_TYPE_STRING -> c.getString(i)
            else -> null
        }
    }

    private fun delete(uri: Uri, rowId: Long) = resolver.delete(ContentUris(uri, rowId), null, null)

    @Suppress("FunctionName")
    private fun ContentUris(base: Uri, id: Long): Uri = android.content.ContentUris.withAppendedId(base, id).buildUpon()
        .encodedQuery(base.encodedQuery).build()

    companion object {
        private const val TAG = "PlanItSystemCalendar"
        const val KEY_ID = "planit_account_id"
        private const val BATCH = 200
        private val DEFAULT_COLOR = 0xFF5B5BD6.toInt()

        private val IMPORT_COLUMNS = arrayOf(
            Events._ID, Events._SYNC_ID, Events.ORIGINAL_SYNC_ID, Events.ORIGINAL_INSTANCE_TIME, Events.CALENDAR_ID, Events.DELETED,
            Events.DIRTY, Events.STATUS, Events.TITLE, Events.DESCRIPTION, Events.EVENT_LOCATION, Events.DTSTART, Events.DTEND,
            Events.DURATION, Events.ALL_DAY, Events.EVENT_TIMEZONE, Events.RRULE, Events.EXDATE,
        )
    }
}
