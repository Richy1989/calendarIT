package org.hyperstarit.twentyfourseven.sync

import androidx.room.withTransaction
import java.io.IOException
import java.time.Clock
import kotlinx.serialization.json.Json
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.EventEntity
import org.hyperstarit.twentyfourseven.data.db.OutboxEntity
import org.hyperstarit.twentyfourseven.data.db.OverrideEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.data.db.TaskEntity
import org.hyperstarit.twentyfourseven.net.OpStatus
import org.hyperstarit.twentyfourseven.net.SyncOp
import org.hyperstarit.twentyfourseven.net.SyncOpResult
import org.hyperstarit.twentyfourseven.net.SyncPullResponse

/** The server speaks a sync protocol this app doesn't. */
class ProtocolException(message: String) : IOException(message)

/**
 * Syncs one CalendarIT account (protocol 1): first the queued changes go up, then what changed on the
 * server comes down, then pending edits the server has taken make way for its copy.
 */
class CalendarItSync(
    private val db: AppDatabase,
    private val accounts: Accounts,
    private val clock: Clock,
    private val changes: LocalChanges,
) {
    suspend fun sync(account: AccountEntity) {
        push(account)
        pull(account)
        db.withTransaction {
            db.events().dropSettledEdits()
            db.events().dropSettledOverrideEdits()
            db.tasks().dropSettledEdits()
        }
    }

    // ---------------------------------------------------------------- push

    private suspend fun push(account: AccountEntity) {
        val auth = accounts.auth(account)
        val api = accounts.api(account.certificatePin)
        db.outbox().clearSending() // a sync that died half-way left its flags behind
        while (true) {
            val now = clock.millis()
            // In order, up to the first change that waits for a retry: later ones may depend on it.
            val batch = db.outbox().forAccount(account.id).takeWhile { it.nextAttemptAt <= now }.take(MAX_OPS)
            if (batch.isEmpty()) return
            db.outbox().update(batch.map { it.copy(sending = true) })
            val response = try {
                auth.authorized { server, token -> api.push(server, token, batch.map { it.wire() }) }
            } catch (e: Exception) {
                db.outbox().clearSending()
                throw e
            }
            val failed = db.withTransaction { settle(account, batch, response.results) }
            db.outbox().clearSending()
            if (failed) return // the server had trouble: the rest waits for the retry
        }
    }

    private fun OutboxEntity.wire() = SyncOp(
        opId = opId, op = op, id = entityId, baseVersion = baseVersion, occurrence = occurrence,
        data = data?.let { Json.parseToJsonElement(it) },
    )

    /** Applies the server's answers; true when one of them asks to try again later. */
    private suspend fun settle(account: AccountEntity, batch: List<OutboxEntity>, results: List<SyncOpResult>): Boolean {
        val byOpId = results.associateBy { it.opId }
        val versions = mutableMapOf<String, String>()
        val dropped = mutableSetOf<String>() // entities whose queue was abandoned
        var failed = false
        for (op in batch) {
            if (op.entityId in dropped) continue
            val result = byOpId[op.opId] ?: continue // not answered: stays queued
            when (result.status) {
                OpStatus.OK -> {
                    db.outbox().delete(listOf(op.seq))
                    result.versions?.let(versions::putAll)
                }
                // A create the server already has came again after a lost answer (ids are random).
                OpStatus.CONFLICT -> if (op.op in Ops.creates) {
                    db.outbox().delete(listOf(op.seq))
                } else {
                    abandon(account, op, "conflict", "It was changed elsewhere first; that version was kept.", keepForRetry = true)
                    dropped += op.entityId
                }
                OpStatus.NOT_FOUND -> {
                    db.outbox().delete(listOf(op.seq))
                    if (op.op !in Ops.deletes) {
                        abandon(account, op, "gone", "It was deleted elsewhere.", keepForRetry = false)
                        dropped += op.entityId
                    }
                }
                OpStatus.INVALID, OpStatus.FORBIDDEN -> {
                    abandon(account, op, "rejected", result.error ?: "The server didn't accept this change.", keepForRetry = false)
                    dropped += op.entityId
                }
                else -> {
                    val attempts = op.attempts + 1
                    db.outbox().update(
                        listOf(op.copy(attempts = attempts, nextAttemptAt = clock.millis() + backoff(attempts), lastError = result.error, sending = false)),
                    )
                    failed = true
                }
            }
        }
        // Changes still queued for something the server just took are now made on its new version.
        for ((entityId, version) in versions) {
            val queued = db.outbox().forEntity(entityId)
            if (queued.isNotEmpty()) db.outbox().update(queued.map { it.copy(baseVersion = version) })
        }
        return failed
    }

    /** Drops every queued change for [op]'s thing and tells the user why. */
    private suspend fun abandon(account: AccountEntity, op: OutboxEntity, kind: String, message: String, keepForRetry: Boolean) {
        val queued = db.outbox().forEntity(op.entityId)
        db.outbox().delete(queued.map { it.seq })
        val title = when (op.entityKind) {
            EntityKind.EVENT -> (db.events().edit(op.entityId)?.data ?: db.events().server(op.entityId)?.data)?.title
            else -> (db.tasks().edit(op.entityId)?.data ?: db.tasks().server(op.entityId)?.data)?.title
        } ?: "An item"
        // "Apply mine anyway" re-sends the last whole edit; a refused delete can be sent again as it is.
        val retry = if (keepForRetry) queued.lastOrNull { it.op !in Ops.creates } else null
        db.notices().insert(
            changes.notice(account.id, kind, op.entityKind, op.entityId, title, message, retry?.op, retry?.data),
        )
    }

    // ---------------------------------------------------------------- pull

    private suspend fun pull(account: AccountEntity) {
        val auth = accounts.auth(account)
        val api = accounts.api(account.certificatePin)
        var cursor = account.syncCursor
        var full = cursor == null
        val seenEvents = HashSet<String>()
        val seenTasks = HashSet<String>()
        while (true) {
            val sent = cursor
            val page = auth.authorized { server, token -> api.pull(server, token, sent, PAGE) }
            if (page.protocol != PROTOCOL) {
                throw ProtocolException("This server speaks sync protocol ${page.protocol}; update 24seven to sync with it.")
            }
            if (page.reset) {
                full = true
                seenEvents.clear()
                seenTasks.clear()
            }
            seenEvents += page.events.map { it.id }
            seenTasks += page.tasks.map { it.id }
            db.withTransaction { apply(account.id, page) }
            cursor = page.cursor
            if (!page.hasMore) break
        }
        db.withTransaction {
            if (full) {
                // A full listing is everything there is: what it didn't name is gone.
                val goneEvents = db.events().serverIds(account.id).filterNot { it in seenEvents }
                goneEvents.chunked(CHUNK).forEach {
                    db.events().deleteServer(it)
                    db.events().deleteOverridesOf(it)
                }
                db.tasks().serverIds(account.id).filterNot { it in seenTasks }.chunked(CHUNK).forEach { db.tasks().deleteServer(it) }
            }
            db.accounts().setSynced(account.id, cursor, clock.millis())
        }
    }

    private suspend fun apply(accountId: String, page: SyncPullResponse) {
        // Calendars and categories come whole: one that isn't listed is gone, with what was in it.
        db.calendars().upsert(page.calendars.mapIndexed { i, c -> c.row(accountId, i) })
        db.calendars().deleteOthers(accountId, page.calendars.map { it.id })
        db.events().orphans(accountId).chunked(CHUNK).forEach {
            db.events().deleteServer(it)
            db.events().deleteOverridesOf(it)
        }
        db.tasks().orphans(accountId).chunked(CHUNK).forEach { db.tasks().deleteServer(it) }
        db.categories().upsert(page.categories.map { it.row(accountId) })
        db.categories().deleteOthers(accountId, page.categories.map { it.id })

        if (page.events.isNotEmpty()) {
            db.events().upsert(page.events.map { EventEntity(it.row(accountId)) })
            page.events.map { it.id }.chunked(CHUNK).forEach { db.events().deleteOverridesOf(it) }
            db.events().insertOverrides(page.events.flatMap { e -> e.overrideRows().map { OverrideEntity(it) } })
        }
        if (page.tasks.isNotEmpty()) db.tasks().upsert(page.tasks.map { TaskEntity(it.row(accountId)) })
        page.deleted.events.chunked(CHUNK).forEach {
            db.events().deleteServer(it)
            db.events().deleteOverridesOf(it)
        }
        page.deleted.tasks.chunked(CHUNK).forEach { db.tasks().deleteServer(it) }
    }

    companion object {
        const val PROTOCOL = 1
        const val PAGE = 500
        const val MAX_OPS = 100
        private const val CHUNK = 500 // SQLite's parameter limit stays far away

        /** 30 s, doubling per failure, at most an hour. */
        fun backoff(attempts: Int): Long = (30_000L shl (attempts - 1).coerceIn(0, 7)).coerceAtMost(3_600_000L)
    }
}
