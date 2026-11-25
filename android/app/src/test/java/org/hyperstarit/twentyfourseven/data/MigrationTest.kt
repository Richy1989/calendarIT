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
}
