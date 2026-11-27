package org.hyperstarit.twentyfourseven.caldav

import java.time.ZoneId
import org.hyperstarit.twentyfourseven.data.db.Attendee
import org.hyperstarit.twentyfourseven.data.db.EventData
import org.hyperstarit.twentyfourseven.data.db.OverrideData
import org.hyperstarit.twentyfourseven.data.db.Reminder
import org.hyperstarit.twentyfourseven.data.db.TaskData
import org.hyperstarit.twentyfourseven.reminders.ReminderPlanner

/** Where a resource is read: whose, which calendar, the clock of floating times, and who "me" is among guests. */
data class DavContext(val accountId: String, val calendarId: String, val floating: ZoneId, val me: Set<String>)

sealed interface DavItem {
    val uid: String
}

/** A repeating series (or one-off) with its edited occurrences, as one resource holds them. */
data class DavSeries(override val uid: String, val event: EventData, val overrides: List<OverrideData>) : DavItem

data class DavTask(override val uid: String, val task: TaskData, val relatedUid: String?) : DavItem

/**
 * iCalendar resources ↔ the app's rows, by the server's rules: an all-day value is its date's
 * midnight UTC with an inclusive end; a series' exclusions hold its deleted *and* edited
 * occurrences (EXDATE on the wire holds only the deleted ones).
 *
 * Writing edits the resource as last read: only what changed is replaced, the rest is kept.
 */
object DavMapping {
    private const val DAY = 86_400_000L
    private const val CATEGORY_PREFIX = "dav:"
    private const val PRODID = "-//CalendarIT//24seven Android//EN"

    /** CalDAV has no category list: a category is its name. */
    fun categoryId(name: String) = CATEGORY_PREFIX + name

    fun categoryName(id: String?): String? = id?.takeIf { it.startsWith(CATEGORY_PREFIX) }?.removePrefix(CATEGORY_PREFIX)

    fun overrideId(seriesId: String, recurrenceId: Long) = "$seriesId@${Math.floorDiv(recurrenceId, 1000L) * 1000L}"

    // ---------------------------------------------------------------- reading

    /** The event series or task in [ics]; null when it holds neither (a journal, garbage). */
    fun read(ics: String, id: String, etag: String?, ctx: DavContext): DavItem? {
        val root = ICal.parse(ics) ?: return null
        val events = root.children("VEVENT")
        if (events.isNotEmpty()) return readSeries(events, id, etag, ctx)
        val todo = root.children("VTODO").firstOrNull() ?: return null
        return readTask(todo, id, etag, ctx)
    }

    private data class Fields(
        val title: String, val description: String?, val location: String?, val categoryId: String?,
        val start: Long, val end: Long?, val allDay: Boolean, val timeZone: String?, val reminders: List<Reminder>,
    )

    private fun fields(c: ICalComponent, floating: ZoneId): Fields? {
        val start = ICalTime.parse(c.prop("DTSTART"), floating) ?: return null
        val allDay = start.allDay
        val dtEnd = ICalTime.parse(c.prop("DTEND"), floating)
        val duration = c.prop("DURATION")?.value?.let(ICalTime::parseDuration)?.toMillis()
        val end = when {
            allDay && dtEnd != null -> (dtEnd.millis - DAY).coerceAtLeast(start.millis)
            allDay && duration != null -> (start.millis + duration - DAY).coerceAtLeast(start.millis)
            allDay -> start.millis
            dtEnd != null -> dtEnd.millis.coerceAtLeast(start.millis)
            duration != null -> start.millis + duration.coerceAtLeast(0)
            else -> null
        }
        return Fields(
            title = c.prop("SUMMARY")?.text.orEmpty(),
            description = c.prop("DESCRIPTION")?.text?.takeIf { it.isNotEmpty() },
            location = c.prop("LOCATION")?.text?.takeIf { it.isNotEmpty() },
            categoryId = categoryNames(c).firstOrNull()?.let(::categoryId),
            start = start.millis,
            end = end,
            allDay = allDay,
            timeZone = if (allDay) null else start.zone,
            reminders = reminders(c, task = false),
        )
    }

    private fun categoryNames(c: ICalComponent): List<String> =
        c.props("CATEGORIES").flatMap { ICal.splitList(it.value) }.map { ICal.unescape(it).trim() }.filter { it.isNotEmpty() }

    /** Alarms the app can ring: a display or sound before the start (a task's: before its due). */
    private fun ours(alarm: ICalComponent, task: Boolean, hasStart: Boolean): Int? {
        val action = alarm.prop("ACTION")?.value?.trim()?.uppercase()
        if (action != "DISPLAY" && action != "AUDIO") return null
        val trigger = alarm.prop("TRIGGER") ?: return null
        if (trigger.param("VALUE").equals("DATE-TIME", true)) return null
        val related = trigger.param("RELATED")?.uppercase() ?: "START"
        if (task) { if (related != "END" && hasStart) return null } else if (related != "START") return null
        val minutes = -(ICalTime.parseDuration(trigger.value) ?: return null).toMinutes()
        return minutes.takeIf { it in 0..(60L * 24 * 366) }?.toInt()
    }

    private fun reminders(c: ICalComponent, task: Boolean): List<Reminder> {
        val hasStart = c.prop("DTSTART") != null
        return c.children("VALARM").mapNotNull { ours(it, task, hasStart) }.distinct().sorted()
            .map { Reminder(it, ReminderPlanner.NOTIFICATION) }
    }

    private fun mail(v: String?): String? = v?.trim()?.let { if (it.startsWith("mailto:", true)) it.substring(7) else it }
        ?.trim()?.lowercase()?.takeIf { it.isNotEmpty() }

    private fun status(partstat: String?): String = when (partstat?.uppercase()) {
        "ACCEPTED" -> "Accepted"
        "DECLINED" -> "Declined"
        "TENTATIVE" -> "Tentative"
        "DELEGATED" -> "Delegated"
        else -> "NeedsAction"
    }

    private fun partstat(status: String): String = when (status) {
        "Accepted" -> "ACCEPTED"
        "Declined" -> "DECLINED"
        "Tentative" -> "TENTATIVE"
        "Delegated" -> "DELEGATED"
        else -> "NEEDS-ACTION"
    }

    private fun readSeries(events: List<ICalComponent>, id: String, etag: String?, ctx: DavContext): DavSeries? {
        // A resource with only edited occurrences (an invitation to one of them) shows its first as a one-off.
        val master = events.firstOrNull { it.prop("RECURRENCE-ID") == null } ?: events.first()
        val isMaster = master.prop("RECURRENCE-ID") == null
        val f = fields(master, ctx.floating) ?: return null
        val uid = master.prop("UID")?.value?.trim()?.takeIf { it.isNotEmpty() } ?: id
        val rule = if (isMaster) master.prop("RRULE")?.value?.trim()?.takeIf(::usableRule) else null
        val attendees = master.props("ATTENDEE").mapNotNull { a ->
            mail(a.value)?.let { Attendee(it, a.param("CN")?.takeIf(String::isNotBlank), status(a.param("PARTSTAT"))) }
        }
        val organizer = mail(master.prop("ORGANIZER")?.value)
        val mine = attendees.firstOrNull { it.email in ctx.me }
        val invitation = if (organizer != null && organizer !in ctx.me && mine != null) mine.status else null

        val overrides = if (rule == null) emptyList() else events.filter { it !== master && it.prop("RECURRENCE-ID") != null }.mapNotNull { o ->
            val rid = ICalTime.parse(o.prop("RECURRENCE-ID"), ctx.floating)?.millis ?: return@mapNotNull null
            val of = fields(o, ctx.floating) ?: return@mapNotNull null
            OverrideData(
                id = overrideId(id, rid), accountId = ctx.accountId, seriesId = id, recurrenceId = truncate(rid),
                title = of.title, description = of.description, location = of.location, categoryId = of.categoryId,
                start = of.start, end = of.end, allDay = of.allDay, timeZone = of.timeZone ?: f.timeZone, reminders = of.reminders,
            )
        }.distinctBy { it.recurrenceId }
        val exDates = if (rule == null) emptyList() else
            (master.props("EXDATE").flatMap { ICalTime.parseAll(it, ctx.floating) }.map { truncate(it.millis) } + overrides.map { it.recurrenceId })
                .distinct().sorted()
        val event = EventData(
            id = id, accountId = ctx.accountId, calendarId = ctx.calendarId, title = f.title, description = f.description,
            location = f.location, categoryId = f.categoryId, color = master.prop("COLOR")?.value?.let(DavClient::cssColor),
            start = f.start, end = f.end, allDay = f.allDay, timeZone = f.timeZone, recurrence = rule, exDates = exDates,
            reminders = f.reminders, attendees = attendees, invitationStatus = invitation, organizerEmail = organizer, version = etag,
        )
        return DavSeries(uid, event, overrides)
    }

    /** As on the server: a rule that repeats more often than hourly isn't one this app expands. */
    private fun usableRule(rule: String): Boolean {
        val freq = rule.uppercase().split(';').firstOrNull { it.startsWith("FREQ=") }?.substringAfter('=') ?: return false
        return freq in setOf("HOURLY", "DAILY", "WEEKLY", "MONTHLY", "YEARLY")
    }

    private fun readTask(c: ICalComponent, id: String, etag: String?, ctx: DavContext): DavTask {
        val due = ICalTime.parse(c.prop("DUE"), ctx.floating)
        val status = when (c.prop("STATUS")?.value?.trim()?.uppercase()) {
            "COMPLETED" -> "Completed"
            "IN-PROCESS" -> "InProcess"
            "CANCELLED" -> "Cancelled"
            else -> if (c.prop("COMPLETED") != null) "Completed" else "NeedsAction"
        }
        val priority = when (c.prop("PRIORITY")?.value?.trim()?.toIntOrNull()) {
            in 1..4 -> "high"
            5 -> "normal"
            in 6..9 -> "low"
            else -> null
        }
        val parent = c.props("RELATED-TO").firstOrNull { (it.param("RELTYPE") ?: "PARENT").equals("PARENT", true) }
            ?.value?.trim()?.takeIf { it.isNotEmpty() }
        val task = TaskData(
            id = id, accountId = ctx.accountId, calendarId = ctx.calendarId, title = c.prop("SUMMARY")?.text.orEmpty(),
            notes = c.prop("DESCRIPTION")?.text?.takeIf { it.isNotEmpty() }, due = due?.millis, dueAllDay = due?.allDay ?: false,
            timeZone = due?.zone?.takeIf { !due.allDay }, status = status,
            completedAt = if (status == "Completed") ICalTime.parse(c.prop("COMPLETED"), ZoneId.of("UTC"))?.millis else null,
            priority = priority, parentId = null, sortOrder = c.prop("X-APPLE-SORT-ORDER")?.value?.trim()?.toIntOrNull() ?: 0,
            reminders = reminders(c, task = true), version = etag,
        )
        return DavTask(c.prop("UID")?.value?.trim()?.takeIf { it.isNotEmpty() } ?: id, task, parent)
    }

    private fun truncate(millis: Long) = Math.floorDiv(millis, 1000L) * 1000L

    // ---------------------------------------------------------------- writing

    private fun newCalendar() = ICalComponent("VCALENDAR").apply {
        set("VERSION", "2.0")
        set("PRODID", PRODID)
    }

    /** [raw] (the resource as last read, null for a new one) with the series written over it. */
    fun writeSeries(raw: String?, uid: String, event: EventData, overrides: List<OverrideData>, ctx: DavContext, now: Long): String {
        val root = raw?.let(ICal::parse) ?: newCalendar()
        val existing = root.children("VEVENT")
        var master = existing.firstOrNull { it.prop("RECURRENCE-ID") == null }
        val wasOneInstance = master == null && existing.isNotEmpty()
        if (wasOneInstance) master = existing.first() // the one-off shown from a lone occurrence
        val created = master == null
        val m = master ?: ICalComponent("VEVENT").also { root.components += it }
        val old = if (created) null else fields(m, ctx.floating)
        val oldRule = if (wasOneInstance) null else m.prop("RRULE")?.value?.trim()
        if (created) {
            m.set("UID", uid)
            m.set("CREATED", ICalTime.utc(now))
        }
        val new = Fields(
            event.title, event.description, event.location, event.categoryId, event.start, event.end, event.allDay,
            if (event.allDay) null else event.timeZone, event.reminders,
        )
        apply(m, old, new, now, task = false)
        if (!wasOneInstance && event.recurrence?.trim() != oldRule?.takeIf(::usableRule)) {
            m.set("RRULE", event.recurrence)
            if (old != null) bumpSequence(m)
        }
        attendees(m, event, ctx, created)

        if (wasOneInstance || event.recurrence == null) {
            m.remove("EXDATE")
            root.components.removeAll { it.name == "VEVENT" && it !== m }
        } else {
            val overrideIds = overrides.map { truncate(it.recurrenceId) }.toSet()
            val wanted = event.exDates.map(::truncate).filter { it !in overrideIds }.distinct().sorted()
            val had = m.props("EXDATE").flatMap { ICalTime.parseAll(it, ctx.floating) }.map { truncate(it.millis) }.distinct().sorted()
            val formatChanged = old != null && (old.allDay != new.allDay || old.timeZone != new.timeZone)
            if (wanted != had || formatChanged) {
                m.remove("EXDATE")
                if (wanted.isNotEmpty()) {
                    val values = wanted.map { ICalTime.format(it, new.allDay, new.timeZone) }
                    m.properties += ICalProperty("EXDATE", values.first().second.toMutableList(), values.joinToString(",") { it.first })
                }
            }
            val byRid = existing.filter { it !== m }.associateBy { o ->
                ICalTime.parse(o.prop("RECURRENCE-ID"), ctx.floating)?.millis?.let(::truncate)
            }
            val keep = HashSet<ICalComponent>()
            for (o in overrides) {
                val rid = truncate(o.recurrenceId)
                val comp = byRid[rid] ?: ICalComponent("VEVENT").also { c ->
                    c.set("UID", uid)
                    val (value, params) = ICalTime.format(rid, new.allDay, new.timeZone)
                    c.set("RECURRENCE-ID", value, params)
                    // An edited occurrence keeps the series' guests.
                    m.props("ORGANIZER").plus(m.props("ATTENDEE")).forEach { p -> c.properties += ICalProperty(p.name, p.params.toMutableList(), p.value) }
                    root.components += c
                }
                val oldO = if (byRid[rid] == null) null else fields(comp, ctx.floating)
                apply(
                    comp, oldO,
                    Fields(o.title, o.description, o.location, o.categoryId, o.start, o.end, o.allDay, if (o.allDay) null else o.timeZone ?: new.timeZone, o.reminders),
                    now, task = false,
                )
                if (event.invitationStatus != null) setPartstat(comp, ctx.me, event.invitationStatus)
                keep += comp
            }
            root.components.removeAll { it.name == "VEVENT" && it !== m && it !in keep }
        }
        addTimeZones(root)
        return ICal.write(root)
    }

    /** [raw] (null for a new one) with the task written over it; [parentUid] its RELATED-TO. */
    fun writeTask(raw: String?, uid: String, task: TaskData, parentUid: String?, ctx: DavContext, now: Long): String {
        val root = raw?.let(ICal::parse) ?: newCalendar()
        val existing = root.children("VTODO").firstOrNull()
        val t = existing ?: ICalComponent("VTODO").also {
            it.set("UID", uid)
            it.set("CREATED", ICalTime.utc(now))
            root.components += it
        }
        val old = existing?.let { readTask(it, task.id, null, ctx) }
        val o = old?.task
        if (o?.title != task.title) t.setText("SUMMARY", task.title)
        if (o?.notes != task.notes) t.setText("DESCRIPTION", task.notes)
        if (o == null || o.due != task.due || o.dueAllDay != task.dueAllDay || o.timeZone != task.timeZone) {
            if (task.due == null) t.remove("DUE") else {
                val (value, params) = ICalTime.format(task.due, task.dueAllDay, if (task.dueAllDay) null else task.timeZone)
                t.set("DUE", value, params)
                // A start after the new due would make the task invalid.
                ICalTime.parse(t.prop("DTSTART"), ctx.floating)?.let { if (it.millis > task.due) t.remove("DTSTART") }
            }
        }
        if (o?.status != task.status || o.completedAt != task.completedAt) {
            t.set(
                "STATUS",
                when (task.status) {
                    "Completed" -> "COMPLETED"
                    "InProcess" -> "IN-PROCESS"
                    "Cancelled" -> "CANCELLED"
                    else -> "NEEDS-ACTION"
                },
            )
            if (task.status == "Completed") {
                t.set("COMPLETED", ICalTime.utc(task.completedAt ?: now))
                t.set("PERCENT-COMPLETE", "100")
            } else {
                t.remove("COMPLETED")
                if (t.prop("PERCENT-COMPLETE")?.value?.trim() == "100") t.remove("PERCENT-COMPLETE")
            }
        }
        if (o?.priority != task.priority) {
            t.set("PRIORITY", when (task.priority) { "high" -> "1"; "normal" -> "5"; "low" -> "9"; else -> null })
        }
        if (old?.relatedUid != parentUid) {
            t.properties.removeAll { it.name == "RELATED-TO" && (it.param("RELTYPE") ?: "PARENT").equals("PARENT", true) }
            if (parentUid != null) t.properties += ICalProperty("RELATED-TO", mutableListOf(), parentUid)
        }
        if ((o?.sortOrder ?: 0) != task.sortOrder || (o == null && task.sortOrder != 0)) {
            t.set("X-APPLE-SORT-ORDER", task.sortOrder.toString())
        }
        if (o?.reminders != task.reminders) setAlarms(t, task.reminders, task.title, task = true)
        stamp(t, now)
        addTimeZones(root)
        return ICal.write(root)
    }

    private fun apply(c: ICalComponent, old: Fields?, new: Fields, now: Long, task: Boolean) {
        if (old?.title != new.title) c.setText("SUMMARY", new.title)
        if (old?.description != new.description) c.setText("DESCRIPTION", new.description)
        if (old?.location != new.location) c.setText("LOCATION", new.location)
        if (old == null || old.start != new.start || old.end != new.end || old.allDay != new.allDay || old.timeZone != new.timeZone) {
            val (start, startParams) = ICalTime.format(new.start, new.allDay, new.timeZone)
            c.set("DTSTART", start, startParams)
            c.remove("DURATION")
            val end = when {
                new.allDay -> (new.end ?: new.start).coerceAtLeast(new.start) + DAY
                else -> new.end
            }
            if (end == null) c.remove("DTEND") else {
                val (value, params) = ICalTime.format(end, new.allDay, new.timeZone)
                c.set("DTEND", value, params)
            }
            if (old != null) bumpSequence(c)
        }
        if (old?.categoryId != new.categoryId) {
            val names = categoryNames(c).toMutableList()
            categoryName(old?.categoryId)?.let { n -> names.removeAll { it.equals(n, true) } }
            categoryName(new.categoryId)?.let { n -> names.removeAll { it.equals(n, true) }; names.add(0, n) }
            c.remove("CATEGORIES")
            if (names.isNotEmpty()) c.set("CATEGORIES", names.joinToString(",") { ICal.escape(it) })
        }
        if (old?.reminders != new.reminders) setAlarms(c, new.reminders, new.title, task)
        stamp(c, now)
    }

    private fun setAlarms(c: ICalComponent, reminders: List<Reminder>, title: String, task: Boolean) {
        val hasStart = c.prop("DTSTART") != null
        c.components.removeAll { it.name == "VALARM" && ours(it, task, hasStart) != null }
        for (r in reminders.filter { it.channel == ReminderPlanner.NOTIFICATION }.distinctBy { it.minutesBefore }) {
            c.components += ICalComponent("VALARM").apply {
                set("ACTION", "DISPLAY")
                setText("DESCRIPTION", title.ifEmpty { "Reminder" })
                set("TRIGGER", ICalTime.before(r.minutesBefore), if (task) listOf("RELATED" to "END") else emptyList())
            }
        }
    }

    private fun stamp(c: ICalComponent, now: Long) {
        c.set("DTSTAMP", ICalTime.utc(now))
        c.set("LAST-MODIFIED", ICalTime.utc(now))
    }

    private fun bumpSequence(c: ICalComponent) {
        val n = c.prop("SEQUENCE")?.value?.trim()?.toIntOrNull() ?: 0
        c.set("SEQUENCE", (n + 1).toString())
    }

    /** Guests of a new event are written as invited by "me"; on an invitation only "my" answer changes. */
    private fun attendees(m: ICalComponent, event: EventData, ctx: DavContext, created: Boolean) {
        if (created && event.attendees.isNotEmpty()) {
            ctx.me.firstOrNull()?.let { m.set("ORGANIZER", "mailto:$it") }
            for (a in event.attendees) {
                val params = mutableListOf("PARTSTAT" to "NEEDS-ACTION", "RSVP" to "TRUE", "ROLE" to "REQ-PARTICIPANT")
                a.name?.let { params.add(0, "CN" to it) }
                m.properties += ICalProperty("ATTENDEE", params, "mailto:${a.email}")
            }
        }
        val status = event.invitationStatus ?: return
        setPartstat(m, ctx.me, status)
    }

    private fun setPartstat(c: ICalComponent, me: Set<String>, status: String) {
        for (a in c.props("ATTENDEE")) {
            if (mail(a.value) !in me) continue
            if (a.param("PARTSTAT")?.uppercase() == partstat(status)) continue
            a.setParam("PARTSTAT", partstat(status))
            a.setParam("RSVP", null)
        }
    }

    /** A VTIMEZONE for every TZID the resource uses that it doesn't define yet. */
    private fun addTimeZones(root: ICalComponent) {
        val defined = root.children("VTIMEZONE").mapNotNull { it.prop("TZID")?.value?.trim() }.toSet()
        val used = LinkedHashSet<String>()
        fun walk(c: ICalComponent) {
            if (c.name == "VTIMEZONE") return
            c.properties.forEach { p -> p.param("TZID")?.let(used::add) }
            c.components.forEach(::walk)
        }
        root.components.forEach(::walk)
        val at = root.components.indexOfFirst { it.name != "VTIMEZONE" }.let { if (it < 0) root.components.size else it }
        val missing = used.filter { it !in defined }.mapNotNull(ICalTime::vTimeZone)
        root.components.addAll(at, missing)
    }
}
