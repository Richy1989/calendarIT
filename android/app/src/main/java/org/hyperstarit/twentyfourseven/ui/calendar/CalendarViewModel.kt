package org.hyperstarit.twentyfourseven.ui.calendar

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import java.time.LocalDate
import java.time.ZoneId
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.calendar.CalendarPrefs
import org.hyperstarit.twentyfourseven.calendar.DateRange
import org.hyperstarit.twentyfourseven.calendar.Occurrence
import org.hyperstarit.twentyfourseven.calendar.Occurrences
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.CalendarEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.sync.SyncRunner

enum class ViewMode { MONTH, WEEK, DAY, AGENDA }

/** What the views show: every date of [range] with its appointments. */
data class Shown(val range: DateRange, val byDate: Map<LocalDate, List<Occurrence>>) {
    fun on(date: LocalDate): List<Occurrence> = byDate[date].orEmpty()
}

@HiltViewModel
class CalendarViewModel @Inject constructor(
    db: AppDatabase,
    private val prefs: CalendarPrefs,
    private val runner: SyncRunner,
    private val changes: org.hyperstarit.twentyfourseven.sync.LocalChanges,
    private val appScope: kotlinx.coroutines.CoroutineScope,
    private val reminderPrefs: org.hyperstarit.twentyfourseven.reminders.ReminderPrefs,
) : ViewModel() {
    /** True once: the first time the calendar shows, the app asks to show notifications. */
    fun shouldAskForNotifications(): Boolean = !reminderPrefs.askedForPermission.also { reminderPrefs.askedForPermission = true }

    val zone: ZoneId = ZoneId.systemDefault()

    var mode by mutableStateOf(ViewMode.MONTH)
        private set
    private val modeFlow = MutableStateFlow(ViewMode.MONTH)

    /** The date the views are at; the title follows it, "Today" sets it. */
    val focus = MutableStateFlow(LocalDate.now(zone))

    var syncing by mutableStateOf(false)
        private set

    val accounts = db.accounts().observeAll().stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList<AccountEntity>())
    val calendars = db.calendars().observeAll().stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList<CalendarEntity>())
    val hidden = prefs.hidden.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptySet())

    /** The dates expanded: a few months around the focus (more ahead for the agenda) — the same
     * window while the focus stays in one month, so paging within it costs nothing. */
    private val window = combine(focus, modeFlow) { date, mode ->
        val month = date.withDayOfMonth(1)
        if (mode == ViewMode.AGENDA) DateRange(month.minusMonths(1), month.plusMonths(7))
        else DateRange(month.minusMonths(1), month.plusMonths(2))
    }.distinctUntilChanged()

    private val stored = combine(
        db.events().observeLive(), db.events().observeLiveOverrides(), db.calendars().observeAll(), db.categories().observeAll(), prefs.hidden,
    ) { events, overrides, calendars, categories, hidden -> Stored(events, overrides, calendars, categories, hidden) }

    private data class Stored(
        val events: List<org.hyperstarit.twentyfourseven.data.db.EventEntity>,
        val overrides: List<org.hyperstarit.twentyfourseven.data.db.OverrideEntity>,
        val calendars: List<CalendarEntity>,
        val categories: List<org.hyperstarit.twentyfourseven.data.db.CategoryEntity>,
        val hidden: Set<String>,
    )

    /** A delete the user can still undo: hidden at once, done when the undo offer runs out. */
    data class PendingDelete(val occurrence: Occurrence, val scope: org.hyperstarit.twentyfourseven.ui.editor.Scope?) {
        fun hides(o: Occurrence): Boolean {
            val series = occurrence.seriesId
            return when {
                series == null -> o.eventId == occurrence.eventId
                scope == org.hyperstarit.twentyfourseven.ui.editor.Scope.ONE -> o.key == occurrence.key
                scope == org.hyperstarit.twentyfourseven.ui.editor.Scope.FOLLOWING -> o.seriesId == series && (o.recurrenceId ?: 0) >= occurrence.recurrenceId!!
                else -> o.seriesId == series
            }
        }
    }

    val pendingDelete = MutableStateFlow<PendingDelete?>(null)

    val shown = combine(stored, window, pendingDelete) { s, range, pending ->
        val list = Occurrences.build(
            s.events.map { it.data }, s.overrides.map { it.data }, s.calendars.associateBy { it.id },
            s.categories.associateBy { it.id }, s.hidden, range, zone,
        ).filterNot { pending?.hides(it) == true }
        Shown(range, Occurrences.byDate(list, range, zone))
    }.flowOn(Dispatchers.Default).stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    init {
        viewModelScope.launch {
            prefs.view.first()?.let { saved -> ViewMode.entries.firstOrNull { it.name == saved }?.let(::show) }
        }
    }

    fun show(mode: ViewMode) {
        this.mode = mode
        modeFlow.value = mode
        viewModelScope.launch { prefs.setView(mode.name) }
    }

    fun openDay(date: LocalDate) {
        focus.value = date
        show(ViewMode.DAY)
    }

    fun today() {
        focus.value = LocalDate.now(zone)
    }

    /** The ‹ › arrows: a month, a week or a day back or ahead, as the view shows. */
    fun step(forward: Boolean) {
        val d = if (forward) 1L else -1L
        focus.value = when (mode) {
            ViewMode.MONTH -> focus.value.plusMonths(d)
            ViewMode.WEEK, ViewMode.AGENDA -> focus.value.plusWeeks(d)
            ViewMode.DAY -> focus.value.plusDays(d)
        }
    }

    fun setHidden(calendarId: String, hidden: Boolean) {
        viewModelScope.launch { prefs.setHidden(calendarId, hidden) }
    }

    fun delete(o: Occurrence, scope: org.hyperstarit.twentyfourseven.ui.editor.Scope?) {
        commitPendingDelete()
        pendingDelete.value = PendingDelete(o, scope)
    }

    fun undoDelete() {
        pendingDelete.value = null
    }

    /** The undo offer ran out (or another delete came): now it really goes. */
    fun commitPendingDelete() {
        val p = pendingDelete.value ?: return
        pendingDelete.value = null
        viewModelScope.launch { perform(p) }
    }

    fun respond(o: Occurrence, status: String) {
        viewModelScope.launch { changes.respond(o.seriesId ?: o.eventId, status) }
    }

    private suspend fun perform(p: PendingDelete) {
        val o = p.occurrence
        val series = o.seriesId
        when {
            series == null -> changes.deleteEvent(o.eventId)
            p.scope == org.hyperstarit.twentyfourseven.ui.editor.Scope.ONE -> changes.deleteOccurrence(series, o.recurrenceId!!)
            p.scope == org.hyperstarit.twentyfourseven.ui.editor.Scope.FOLLOWING -> changes.deleteFollowing(series, o.recurrenceId!!)
            else -> changes.deleteEvent(series)
        }
    }

    override fun onCleared() {
        // Leaving with an undo still on offer: the delete stands (the app scope outlives this screen).
        pendingDelete.value?.let { p -> appScope.launch { perform(p) } }
    }

    fun syncNow() {
        if (syncing) return
        syncing = true
        viewModelScope.launch {
            try { runner.syncAll() } finally { syncing = false }
        }
    }

}
