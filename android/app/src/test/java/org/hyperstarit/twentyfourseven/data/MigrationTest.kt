package org.hyperstarit.twentyfourseven.data

import androidx.room.testing.MigrationTestHelper
import androidx.test.platform.app.InstrumentationRegistry
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class MigrationTest {
    @get:Rule
    val helper = MigrationTestHelper(InstrumentationRegistry.getInstrumentation(), AppDatabase::class.java)

    @Test
    fun version1_BecomesVersion2_AsAFreshInstallWouldBe() {
        helper.createDatabase("m.db", 1).apply {
            execSQL("INSERT INTO calendars (id, name, color, timeZone, defaultCategoryId, readOnly, sortOrder, version) VALUES ('c', 'P', NULL, NULL, NULL, 0, 0, '1')")
            close()
        }
        // Validates every table, column and index against the exported version-2 schema.
        val db = helper.runMigrationsAndValidate("m.db", 2, true, AppDatabase.MIGRATION_1_2)
        db.query("SELECT COUNT(*) FROM calendars").use { it.moveToFirst(); assertEquals(0, it.getInt(0)) } // refilled by the next sync
    }

    @Test
    fun version2_BecomesVersion3_KeepingEveryRow_AndOverridesLearnTheirAccount() {
        val ev = "(localDeleted, id, accountId, calendarId, title, start, allDay, exDates, reminders, attendees, version)"
        val ov = "(localDeleted, id, seriesId, recurrenceId, title, start, allDay, reminders)"
        val tk = "(localDeleted, id, accountId, calendarId, title, dueAllDay, status, sortOrder, reminders, version)"
        helper.createDatabase("m2.db", 2).apply {
            execSQL("INSERT INTO accounts (id, kind, server, username, syncCursor, needsSignIn, certificateChanged, createdAt) VALUES ('A', 'calendarit', 'https://a/', 'me', 'cur-a', 0, 0, 1)")
            execSQL("INSERT INTO accounts (id, kind, server, username, syncCursor, needsSignIn, certificateChanged, createdAt) VALUES ('B', 'calendarit', 'https://b/', 'me', 'cur-b', 0, 0, 2)")
            execSQL("INSERT INTO calendars (id, accountId, name, defaultReminders, readOnly, sortOrder, version) VALUES ('c1', 'A', 'P', '[]', 0, 0, '1')")
            execSQL("INSERT INTO categories (id, accountId, name, color, version) VALUES ('k1', 'A', 'Work', '#fff', '1')")
            execSQL("INSERT INTO events $ev VALUES (0, 'e1', 'A', 'c1', 'Standup', 0, 0, '[]', '[]', '[]', '10')")
            execSQL("INSERT INTO events $ev VALUES (0, 'e2', 'B', 'c9', 'Other', 0, 0, '[]', '[]', '[]', '10')")
            execSQL("INSERT INTO event_edits $ev VALUES (0, 'e3', 'B', 'c9', 'Draft', 0, 0, '[]', '[]', '[]', NULL)")
            execSQL("INSERT INTO overrides $ov VALUES (0, 'o1', 'e1', 5, 'Moved', 5, 0, '[]')")
            execSQL("INSERT INTO overrides $ov VALUES (0, 'o0', 'gone', 5, 'Orphan', 5, 0, '[]')")
            execSQL("INSERT INTO override_edits $ov VALUES (0, 'o2', 'e3', 5, 'Draft moved', 5, 0, '[]')")
            execSQL("INSERT INTO tasks $tk VALUES (0, 't1', 'A', 'c1', 'Milk', 0, 'NeedsAction', 0, '[]', '20')")
            execSQL("INSERT INTO task_edits $tk VALUES (0, 't1', 'A', 'c1', 'Oat milk', 0, 'NeedsAction', 0, '[]', '20')")
            execSQL("INSERT INTO outbox (accountId, opId, op, entityKind, entityId, sending, attempts, nextAttemptAt) VALUES ('B', 'x', 'event.create', 'event', 'e3', 0, 0, 0)")
            execSQL("INSERT INTO notices (accountId, kind, entityKind, entityId, title, message, createdAt) VALUES ('A', 'gone', 'event', 'e9', 'T', 'M', 1)")
            close()
        }
        // Validates every table, column and index against the exported version-3 schema.
        val db = helper.runMigrationsAndValidate("m2.db", 3, true, AppDatabase.MIGRATION_2_3)
        fun rows(sql: String) = db.query(sql).use { c -> buildList { while (c.moveToNext()) add((0 until c.columnCount).joinToString("|") { c.getString(it) ?: "null" }) } }
        assertEquals(listOf("A|c1"), rows("SELECT accountId, id FROM calendars"))
        assertEquals(listOf("A|k1"), rows("SELECT accountId, id FROM categories"))
        assertEquals(listOf("A|e1|Standup", "B|e2|Other"), rows("SELECT accountId, id, title FROM events ORDER BY id"))
        assertEquals(listOf("B|e3|Draft"), rows("SELECT accountId, id, title FROM event_edits"))
        assertEquals(listOf("A|o1|e1"), rows("SELECT accountId, id, seriesId FROM overrides")) // the orphan was never shown
        assertEquals(listOf("B|o2|e3"), rows("SELECT accountId, id, seriesId FROM override_edits"))
        assertEquals(listOf("A|t1|Milk"), rows("SELECT accountId, id, title FROM tasks"))
        assertEquals(listOf("A|t1|Oat milk"), rows("SELECT accountId, id, title FROM task_edits"))
        assertEquals(listOf("B|e3"), rows("SELECT accountId, entityId FROM outbox"))
        assertEquals(listOf("A"), rows("SELECT accountId FROM notices"))
        // Two accounts: rows may have been mixed up before, so each pulls a full listing again.
        assertEquals(listOf("A|null", "B|null"), rows("SELECT id, syncCursor FROM accounts ORDER BY id"))
    }

    @Test
    fun version2_WithOneAccount_KeepsItsCursor() {
        helper.createDatabase("m3.db", 2).apply {
            execSQL("INSERT INTO accounts (id, kind, server, username, syncCursor, needsSignIn, certificateChanged, createdAt) VALUES ('A', 'calendarit', 'https://a/', 'me', 'cur-a', 0, 0, 1)")
            close()
        }
        val db = helper.runMigrationsAndValidate("m3.db", 3, true, AppDatabase.MIGRATION_2_3)
        db.query("SELECT syncCursor FROM accounts").use { it.moveToFirst(); assertEquals("cur-a", it.getString(0)) }
    }

    @Test
    fun version1_ReachesVersion3() {
        helper.createDatabase("m4.db", 1).close()
        helper.runMigrationsAndValidate("m4.db", 3, true, AppDatabase.MIGRATION_1_2, AppDatabase.MIGRATION_2_3)
    }
}
