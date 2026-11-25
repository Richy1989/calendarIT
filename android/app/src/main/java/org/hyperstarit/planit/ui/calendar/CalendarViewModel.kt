package org.hyperstarit.planit.ui.calendar

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
import org.hyperstarit.planit.calendar.CalendarPrefs
import org.hyperstarit.planit.calendar.DateRange
import org.hyperstarit.planit.calendar.Occurrence
import org.hyperstarit.planit.calendar.Occurrences
import org.hyperstarit.planit.data.db.AccountEntity
import org.hyperstarit.planit.data.db.CalendarEntity
import org.hyperstarit.planit.data.db.PlanItDatabase
import org.hyperstarit.planit.sync.SyncRunner

enum class ViewMode { MONTH, WEEK, DAY, AGENDA }

/** What the views show: every date of [range] with its appointments. */
data class Shown(val range: DateRange, val byDate: Map<LocalDate, List<Occurrence>>) {
    fun on(date: LocalDate): List<Occurrence> = byDate[date].orEmpty()
}

@HiltViewModel
class CalendarViewModel @Inject constructor(
    db: PlanItDatabase,
    private val prefs: CalendarPrefs,
    private val runner: SyncRunner,
) : ViewModel() {
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
        val events: List<org.hyperstarit.planit.data.db.EventEntity>,
        val overrides: List<org.hyperstarit.planit.data.db.OverrideEntity>,
        val calendars: List<CalendarEntity>,
        val categories: List<org.hyperstarit.planit.data.db.CategoryEntity>,
        val hidden: Set<String>,
    )

    val shown = combine(stored, window) { s, range ->
        val list = Occurrences.build(
            s.events.map { it.data }, s.overrides.map { it.data }, s.calendars.associateBy { it.id },
            s.categories.associateBy { it.id }, s.hidden, range, zone,
        )
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

    fun setHidden(calendarId: String, hidden: Boolean) {
        viewModelScope.launch { prefs.setHidden(calendarId, hidden) }
    }

    fun syncNow() {
        if (syncing) return
        syncing = true
        viewModelScope.launch {
            try { runner.syncAll() } finally { syncing = false }
        }
    }

}
