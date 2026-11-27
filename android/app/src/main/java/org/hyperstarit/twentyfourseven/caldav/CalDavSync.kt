package org.hyperstarit.twentyfourseven.caldav

import androidx.room.withTransaction
import java.io.IOException
import java.time.Clock
import java.time.ZoneId
import java.util.UUID
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.CategoryEntity
import org.hyperstarit.twentyfourseven.data.db.DavAccountEntity
import org.hyperstarit.twentyfourseven.data.db.DavCalendarEntity
import org.hyperstarit.twentyfourseven.data.db.DavResourceEntity
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.EventEntity
import org.hyperstarit.twentyfourseven.data.db.OutboxEntity
import org.hyperstarit.twentyfourseven.data.db.OverrideData
import org.hyperstarit.twentyfourseven.data.db.OverrideEntity
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.hyperstarit.twentyfourseven.data.db.TaskData
import org.hyperstarit.twentyfourseven.data.db.TaskEntity
import org.hyperstarit.twentyfourseven.sync.CalendarItSync
import org.hyperstarit.twentyfourseven.sync.EntityKind
import org.hyperstarit.twentyfourseven.sync.LocalChanges
import org.hyperstarit.twentyfourseven.sync.Ops

/**
 * Syncs a CalDAV account: the same local store, pending edits and outbox as a 24seven account,
 * spoken to a plain CalDAV server (Nextcloud, Radicale, Baïkal, iCloud, …).
 *
 * Push: the queued changes to one series or task are already folded into its pending edit (with
 * the predicted overrides), so each is sent as the whole resource — PUT with If-Match on the
 * version the user edited (412 = changed elsewhere: the server's copy wins, as with 24seven), or
 * DELETE. "This and following" is two resources: the shortened series and the new one.
 *
 * Pull: per calendar, what changed since the last sync token (RFC 6578), or by comparing every
 * ETag where the server has no tokens; then the changed resources in batches (calendar-multiget).
 */
class CalDavSync(
    private val db: AppDatabase,
    private val clients: suspend (AccountEntity) -> DavClient,
    private val clock: Clock,
    private val changes: LocalChanges,
    private val localZone: () -> ZoneId = { ZoneId.systemDefault() },
) {
    private val json = Json { ignoreUnknownKeys = true }

    suspend fun sync(account: AccountEntity) {
        val client = clients(account)
        val dav = db.dav().account(account.id) ?: throw IOException("This account isn't set up. Remove it and add it again.")
        push(account, client, dav)
        pull(account, client, dav)
        db.withTransaction {
            db.events().dropSettledEdits(account.id)
            db.events().dropSettledOverrideEdits(account.id)
            db.tasks().dropSettledEdits(account.id)
        }
    }

    private suspend fun context(account: AccountEntity, dav: DavAccountEntity, calendarId: String): DavContext {
        val calendar = db.calendars().forAccount(account.id).firstOrNull { it.id == calendarId }
        val zone = calendar?.timeZone?.let { runCatching { ZoneId.of(it) }.getOrNull() } ?: localZone()
        return DavContext(account.id, calendarId, zone, me(account, dav))
    }

    private fun me(account: AccountEntity, dav: DavAccountEntity): Set<String> =
        (dav.userAddresses.lines() + listOfNotNull(account.username.takeIf { '@' in it }))
            .map { it.trim().lowercase() }.filter { it.isNotEmpty() }.toCollection(LinkedHashSet())

    // ---------------------------------------------------------------- push

    private sealed interface Outcome {
        data object Done : Outcome
        data class Refused(val kind: String, val message: String, val retry: Boolean) : Outcome
    }

    private suspend fun push(account: AccountEntity, client: DavClient, dav: DavAccountEntity) {
        db.outbox().clearSending(account.id)
        val byEntity = db.outbox().forAccount(account.id).groupBy { it.entityKind to it.entityId }
        for ((key, ops) in byEntity) {
            // In order, up to the first change that waits for a retry: later ones may depend on it.
            if (ops.first().nextAttemptAt > clock.millis()) return
            val (kind, id) = key
            val outcome = try {
                if (kind == EntityKind.EVENT) pushEvent(account, client, dav, id, ops) else pushTask(account, client, dav, id, ops)
            } catch (e: DavAuthException) {
                throw e
            } catch (_: DavPreconditionException) {
                Outcome.Refused("conflict", "It was changed elsewhere first; that version was kept.", retry = true)
            } catch (e: DavHttpException) {
                when (e.code) {
                    404, 410 -> Outcome.Refused("gone", "It was deleted elsewhere.", retry = false)
                    400, 403, 409, 415, 422, 507 -> Outcome.Refused("rejected", e.message ?: "The server didn't accept this change.", retry = false)
                    else -> { backoff(ops, e.message); return }
                }
            } catch (e: IOException) {
                backoff(ops, e.message)
                return
            }
            when (outcome) {
                Outcome.Done -> db.outbox().delete(ops.map { it.seq })
                is Outcome.Refused -> abandon(account, ops, outcome)
            }
        }
    }

    private suspend fun backoff(ops: List<OutboxEntity>, error: String?) {
        db.outbox().update(
            ops.map {
                val attempts = it.attempts + 1
                it.copy(attempts = attempts, nextAttemptAt = clock.millis() + CalendarItSync.backoff(attempts), lastError = error, sending = false)
            },
        )
    }

    private suspend fun abandon(account: AccountEntity, ops: List<OutboxEntity>, refused: Outcome.Refused) {
        val op = ops.first()
        db.outbox().delete(ops.map { it.seq })
        val a = account.id
        val title = when (op.entityKind) {
            EntityKind.EVENT -> (db.events().edit(a, op.entityId)?.data ?: db.events().server(a, op.entityId)?.data)?.title
            else -> (db.tasks().edit(a, op.entityId)?.data ?: db.tasks().server(a, op.entityId)?.data)?.title
        } ?: "An item"
        // "Apply mine anyway" re-sends the last whole edit.
        val retry = if (refused.retry) ops.lastOrNull { it.op == Ops.EVENT_UPDATE || it.op == Ops.TASK_UPDATE } else null
        // A refused split also takes back the new series it started.
        ops.filter { it.op == Ops.FOLLOWING_UPDATE }.mapNotNull { newSeriesId(it) }.forEach {
            db.events().deleteEdit(a, it)
            db.events().deleteOverrideEdits(a, it)
        }
        db.notices().insert(changes.notice(a, refused.kind, op.entityKind, op.entityId, title, refused.message, retry?.op, retry?.data))
    }

    private fun newSeriesId(op: OutboxEntity): String? =
        op.data?.let { runCatching { json.parseToJsonElement(it).jsonObject["id"]?.jsonPrimitive?.contentOrNull }.getOrNull() }

    private suspend fun calendarHref(accountId: String, calendarId: String): HttpUrl? =
        db.dav().calendars(accountId).firstOrNull { it.calendarId == calendarId }?.href?.toHttpUrl()

    private fun resourceName(uid: String) =
        (if (uid.matches(Regex("[A-Za-z0-9._@-]{1,200}"))) uid else UUID.randomUUID().toString()) + ".ics"

    private suspend fun pushEvent(account: AccountEntity, client: DavClient, dav: DavAccountEntity, id: String, ops: List<OutboxEntity>): Outcome {
        val a = account.id
        val res = db.dav().resource(a, EntityKind.EVENT, id)
        val base = ops.first().baseVersion
        val edit = db.events().edit(a, id)
        val (event, overrides) = when {
            edit == null -> {
                // A change applied again from a notice: its last whole edit, on the server's copy now.
                val server = db.events().server(a, id)?.data ?: return Outcome.Done
                val data = ops.lastOrNull { it.op == Ops.EVENT_UPDATE }?.data ?: return Outcome.Done
                withRequest(server, json.parseToJsonElement(data).jsonObject) to db.events().serverOverrides(a, id).map { it.data }
            }
            edit.localDeleted -> {
                if (res != null) client.delete(res.href.toHttpUrl(), base ?: res.etag)
                forget(a, EntityKind.EVENT, id)
                return Outcome.Done
            }
            else -> edit.data to db.events().overrideEdits(a, id).filter { !it.localDeleted }.map { it.data }
        }
        writeSeries(account, client, dav, res, event, overrides, base)
        // "This and following" started a new series: it is created now.
        for (newId in ops.filter { it.op == Ops.FOLLOWING_UPDATE }.mapNotNull(::newSeriesId).distinct()) {
            val started = db.events().edit(a, newId)?.data ?: continue
            if (db.dav().resource(a, EntityKind.EVENT, newId) != null) continue
            writeSeries(account, client, dav, null, started, db.events().overrideEdits(a, newId).filter { !it.localDeleted }.map { it.data }, null)
        }
        return Outcome.Done
    }

    private suspend fun writeSeries(
        account: AccountEntity, client: DavClient, dav: DavAccountEntity, res: DavResourceEntity?,
        event: EventData, overrides: List<OverrideData>, base: String?,
    ) {
        val a = account.id
        val target = calendarHref(a, event.calendarId) ?: throw DavHttpException(404, "Its calendar is gone.")
        val ctx = context(account, dav, event.calendarId)
        val uid = res?.uid ?: event.id
        val ics = DavMapping.writeSeries(res?.ics, uid, event, overrides, ctx, clock.millis())
        val (href, etag) = put(client, res, target, uid, event.calendarId, ics, base)
        val item = DavMapping.read(ics, event.id, etag, ctx) as? DavSeries ?: return
        db.withTransaction {
            db.events().upsert(listOf(EventEntity(item.event)))
            db.events().deleteOverridesOf(a, listOf(event.id))
            db.events().insertOverrides(item.overrides.map { OverrideEntity(it) })
            db.dav().putResource(DavResourceEntity(a, EntityKind.EVENT, event.id, event.calendarId, href.toString(), uid, etag, ics))
        }
    }

    /** Writes the resource where it belongs: in place, or created (in a new calendar, then the old copy removed). */
    private suspend fun put(
        client: DavClient, res: DavResourceEntity?, target: HttpUrl, uid: String, calendarId: String, ics: String, base: String?,
    ): Pair<HttpUrl, String?> {
        if (res != null && res.calendarId == calendarId) {
            val href = res.href.toHttpUrl()
            return href to client.put(href, ics, ifMatch = base ?: res.etag, create = false)
        }
        val href = target.resolve(resourceName(uid)) ?: throw IOException("Bad calendar address.")
        val etag = client.put(href, ics, ifMatch = null, create = true)
        if (res != null) runCatching { client.delete(res.href.toHttpUrl(), base ?: res.etag) } // moved: the old copy goes
        return href to etag
    }

    private suspend fun pushTask(account: AccountEntity, client: DavClient, dav: DavAccountEntity, id: String, ops: List<OutboxEntity>): Outcome {
        val a = account.id
        val res = db.dav().resource(a, EntityKind.TASK, id)
        val base = ops.first().baseVersion
        val edit = db.tasks().edit(a, id)
        val task = when {
            edit == null -> {
                val server = db.tasks().server(a, id)?.data ?: return Outcome.Done
                val data = ops.lastOrNull { it.op == Ops.TASK_UPDATE }?.data ?: return Outcome.Done
                withRequest(server, json.parseToJsonElement(data).jsonObject)
            }
            edit.localDeleted -> {
                if (res != null) client.delete(res.href.toHttpUrl(), base ?: res.etag)
                forget(a, EntityKind.TASK, id)
                return Outcome.Done
            }
            else -> edit.data
        }
        writeTask(account, client, dav, res, task, base)
        // Ticking a task off ticked off its open subtasks too (predicted, with no change queued of their own).
        if (ops.any { it.op == Ops.TASK_DONE } && task.status == "Completed") {
            val children = db.tasks().observeLive().first().map { it.data }.filter { it.accountId == a && it.parentId == id }
            for (child in children) {
                if (db.tasks().edit(a, child.id) == null || db.outbox().forEntity(a, child.id).isNotEmpty()) continue
                val childRes = db.dav().resource(a, EntityKind.TASK, child.id) ?: continue
                runCatching { writeTask(account, client, dav, childRes, child, db.tasks().server(a, child.id)?.data?.version) }
            }
        }
        return Outcome.Done
    }

    private suspend fun writeTask(account: AccountEntity, client: DavClient, dav: DavAccountEntity, res: DavResourceEntity?, task: TaskData, base: String?) {
        val a = account.id
        val target = calendarHref(a, task.calendarId) ?: throw DavHttpException(404, "Its list is gone.")
        val ctx = context(account, dav, task.calendarId)
        val uid = res?.uid ?: task.id
        val parentUid = task.parentId?.let { db.dav().resource(a, EntityKind.TASK, it)?.uid ?: it }
        val ics = DavMapping.writeTask(res?.ics, uid, task, parentUid, ctx, clock.millis())
        val (href, etag) = put(client, res, target, uid, task.calendarId, ics, base)
        db.withTransaction {
            db.tasks().upsert(listOf(TaskEntity(task.copy(version = etag))))
            db.dav().putResource(DavResourceEntity(a, EntityKind.TASK, task.id, task.calendarId, href.toString(), uid, etag, ics, parentUid))
        }
    }

    private suspend fun forget(accountId: String, kind: String, id: String) = db.withTransaction {
        if (kind == EntityKind.EVENT) {
            db.events().deleteServer(accountId, listOf(id))
            db.events().deleteOverridesOf(accountId, listOf(id))
        } else {
            db.tasks().deleteServer(accountId, listOf(id))
        }
        db.dav().deleteResource(accountId, kind, id)
    }

    // ---------------------------------------------------------------- pull

    private suspend fun pull(account: AccountEntity, client: DavClient, dav: DavAccountEntity) {
        val a = account.id
        val infos = client.calendars(dav.homeUrl.toHttpUrl())
        val stored = db.dav().calendars(a).associateBy { it.calendarId }
        val rows = infos.sortedWith(compareBy({ it.order ?: Int.MAX_VALUE }, { it.name.orEmpty().lowercase() })).mapIndexed { i, c ->
            CalendarEntity(
                id = c.url.encodedPath, accountId = a, name = c.name ?: c.url.pathSegments.lastOrNull { it.isNotEmpty() } ?: "Calendar",
                color = c.color, timeZone = c.timeZone, defaultCategoryId = null, defaultReminders = emptyList(),
                readOnly = c.readOnly, sortOrder = i, version = c.ctag.orEmpty(), allowsEvents = c.events, allowsTasks = c.tasks,
            )
        }
        val ids = rows.map { it.id }
        db.withTransaction {
            db.calendars().upsert(rows)
            db.calendars().deleteOthers(a, ids)
            db.dav().putCalendars(
                infos.map { c ->
                    val id = c.url.encodedPath
                    DavCalendarEntity(a, id, c.url.toString(), stored[id]?.syncToken, stored[id]?.ctag)
                },
            )
            db.dav().deleteOtherCalendars(a, ids)
            db.dav().deleteResourcesOutside(a, ids)
            db.events().orphans(a).chunked(CHUNK).forEach {
                db.events().deleteServer(a, it)
                db.events().deleteOverridesOf(a, it)
            }
            db.tasks().orphans(a).chunked(CHUNK).forEach { db.tasks().deleteServer(a, it) }
        }
        var tasksChanged = false
        for (info in infos) {
            val id = info.url.encodedPath
            tasksChanged = pullCalendar(account, client, dav, info, stored[id]) || tasksChanged
        }
        db.withTransaction {
            if (tasksChanged) linkSubtasks(a)
            val categories = db.dav().categoryIds(a).mapNotNull { cid ->
                DavMapping.categoryName(cid)?.let { CategoryEntity(cid, a, it, colorFor(it), "") }
            }
            db.categories().upsert(categories)
            db.categories().deleteOthers(a, categories.map { it.id })
            db.accounts().setSynced(a, "", clock.millis())
        }
    }

    /** Pulls one calendar's changes; true when a task was among them. */
    private suspend fun pullCalendar(
        account: AccountEntity, client: DavClient, dav: DavAccountEntity, info: DavCalendarInfo, stored: DavCalendarEntity?,
    ): Boolean {
        val a = account.id
        val calendarId = info.url.encodedPath
        val unchanged = stored != null && ((info.ctag != null && info.ctag == stored.ctag) || (info.syncToken != null && info.syncToken == stored.syncToken))
        if (unchanged) return false

        var full: Boolean
        val listed = LinkedHashMap<HttpUrl, String?>()
        val deleted = ArrayList<HttpUrl>()
        var token: String? = null
        if (info.syncToken != null) {
            var since = stored?.syncToken
            full = since == null
            var rounds = 0
            while (true) {
                val ms = try {
                    client.syncCollection(info.url, since)
                } catch (_: DavInvalidSyncTokenException) {
                    if (since == null) throw IOException("The server refused to list the calendar.")
                    since = null
                    full = true
                    listed.clear()
                    deleted.clear()
                    continue
                }
                var truncated = false
                for (r in ms.responses) {
                    if (r.url == info.url || r.url.encodedPath == info.url.encodedPath) {
                        if (r.status == 507) truncated = true
                        continue
                    }
                    if (r.status == 404 || r.status == 410) deleted += r.url
                    else listed[r.url] = r.props.child(DavClient.DAV, "getetag")?.text
                }
                token = ms.syncToken
                // A truncated answer continues from its token; one that doesn't move on is all there is.
                if (!truncated || token == null || token == since || ++rounds >= 100) break
                since = token
            }
        } else {
            full = true
            listed += client.etags(info.url)
        }

        val known = db.dav().inCalendar(a, calendarId).associateBy { it.href }
        var tasks = false
        db.withTransaction {
            val gone = deleted.mapNotNull { known[it.toString()] } +
                if (full) known.values.filter { it.href.toHttpUrl() !in listed } else emptyList()
            for (r in gone) {
                if (r.kind == EntityKind.TASK) tasks = true
                forget(a, r.kind, r.entityId)
            }
        }
        val fetch = listed.filter { (url, etag) -> known[url.toString()]?.let { it.etag == null || it.etag != etag } ?: true }.keys.toList()
        val ctx = context(account, dav, calendarId)
        for (chunk in fetch.chunked(MULTIGET)) {
            val got = client.multiget(info.url, chunk)
            db.withTransaction {
                for ((r, data) in got) {
                    val href = r.url.toString()
                    val before = known[href] ?: db.dav().byHref(a, href)
                    if (r.status == 404 || r.status == 410) {
                        before?.let { forget(a, it.kind, it.entityId); if (it.kind == EntityKind.TASK) tasks = true }
                        continue
                    }
                    if (data == null) continue
                    val etag = r.props.child(DavClient.DAV, "getetag")?.text
                    val id = before?.entityId ?: UUID.randomUUID().toString()
                    val item = DavMapping.read(data, id, etag, ctx) ?: continue
                    val kind = if (item is DavTask) EntityKind.TASK else EntityKind.EVENT
                    if (before != null && before.kind != kind) forget(a, before.kind, before.entityId)
                    when (item) {
                        is DavSeries -> {
                            db.events().upsert(listOf(EventEntity(item.event)))
                            db.events().deleteOverridesOf(a, listOf(id))
                            db.events().insertOverrides(item.overrides.map { OverrideEntity(it) })
                            db.dav().putResource(DavResourceEntity(a, kind, id, calendarId, href, item.uid, etag, data))
                        }
                        is DavTask -> {
                            tasks = true
                            // The parent is linked once every resource is in (it may come later).
                            val parent = db.tasks().server(a, id)?.data?.parentId
                            db.tasks().upsert(listOf(TaskEntity(item.task.copy(parentId = parent))))
                            db.dav().putResource(DavResourceEntity(a, kind, id, calendarId, href, item.uid, etag, data, item.relatedUid))
                        }
                    }
                }
            }
        }
        db.dav().setSynced(a, calendarId, token, info.ctag)
        return tasks
    }

    /** RELATED-TO → parentId; deeper trees are flattened onto their top task (subtasks are one level deep). */
    private suspend fun linkSubtasks(accountId: String) {
        val resources = db.dav().tasks(accountId)
        val byUid = resources.associateBy { it.uid }
        for (r in resources) {
            var parent = r.relatedUid?.let(byUid::get)
            var hops = 0
            while (parent?.relatedUid != null && hops++ < 10) {
                val up = byUid[parent.relatedUid] ?: break
                if (up.entityId == r.entityId) break
                parent = up
            }
            val parentId = parent?.entityId?.takeIf { it != r.entityId }
            val task = db.tasks().server(accountId, r.entityId)?.data ?: continue
            if (task.parentId != parentId) db.tasks().upsert(listOf(TaskEntity(task.copy(parentId = parentId))))
        }
    }

    // ---------------------------------------------------------------- helpers

    private fun JsonObject.str(key: String): String? = (this[key] as? JsonPrimitive)?.takeIf { it !is JsonNull }?.contentOrNull

    private fun JsonObject.reminders(): List<Reminder>? = (this["reminders"] as? JsonArray)?.mapNotNull { r ->
        val o = r as? JsonObject ?: return@mapNotNull null
        val minutes = (o["minutesBefore"] as? JsonPrimitive)?.intOrNull ?: return@mapNotNull null
        Reminder(minutes, o.str("channel") ?: return@mapNotNull null)
    }

    private fun millisOf(iso: String?): Long? = iso?.let { runCatching { java.time.OffsetDateTime.parse(it).toInstant().toEpochMilli() }.getOrNull() }

    /** A queued SaveEventRequest applied to [e]. */
    private fun withRequest(e: EventData, r: JsonObject) = e.copy(
        calendarId = r.str("calendarId") ?: e.calendarId,
        title = r.str("title") ?: e.title,
        description = r.str("description"),
        location = r.str("location"),
        categoryId = r.str("categoryId"),
        start = millisOf(r.str("start")) ?: e.start,
        end = millisOf(r.str("end")),
        allDay = (r["allDay"] as? JsonPrimitive)?.booleanOrNull ?: e.allDay,
        recurrence = r.str("recurrence"),
        timeZone = r.str("timeZone") ?: e.timeZone,
        reminders = r.reminders() ?: e.reminders,
    )

    /** A queued SaveTaskRequest applied to [t]. */
    private fun withRequest(t: TaskData, r: JsonObject) = t.copy(
        calendarId = r.str("calendarId") ?: t.calendarId,
        title = r.str("title") ?: t.title,
        notes = r.str("notes"),
        due = millisOf(r.str("due")),
        dueAllDay = (r["dueAllDay"] as? JsonPrimitive)?.booleanOrNull ?: t.dueAllDay,
        timeZone = r.str("timeZone"),
        priority = r.str("priority"),
        parentId = r.str("parentId"),
        sortOrder = (r["sortOrder"] as? JsonPrimitive)?.intOrNull ?: t.sortOrder,
        reminders = r.reminders() ?: t.reminders,
    )

    companion object {
        private const val CHUNK = 500
        private const val MULTIGET = 50

        private val PALETTE = listOf("#e5484d", "#f76b15", "#ffc53d", "#30a46c", "#12a594", "#0090ff", "#6e56cf", "#d6409f", "#8d8d86")

        /** A category's colour: CalDAV has none, so one is picked from its name (the same on every phone). */
        fun colorFor(name: String) = PALETTE[Math.floorMod(name.lowercase().hashCode(), PALETTE.size)]
    }
}
