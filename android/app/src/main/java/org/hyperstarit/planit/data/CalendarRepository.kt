package org.hyperstarit.planit.data

import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.CategoryEntity
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.net.ApiClient

/**
 * Calendars and categories on the device. For now it reads them from the first page of a sync
 * pull; the full sync engine (events, tasks, the outbox) takes this over in phase 2.
 */
class CalendarRepository(
    private val db: PlanItDatabase,
    private val api: ApiClient,
    private val session: SessionManager,
) {
    val calendars = db.calendars().observeAll()

    suspend fun refresh() {
        val page = session.authorized { server, token -> api.pull(server, token, cursor = null, limit = 50) }
        db.calendars().replaceAll(page.calendars.mapIndexed { index, c ->
            CalendarEntity(c.id, c.name, c.color, c.timeZone, c.defaultCategoryId, c.readOnly, index, c.version)
        })
        db.categories().replaceAll(page.categories.map { CategoryEntity(it.id, it.name, it.color, it.version) })
    }
}
