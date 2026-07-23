// Small client-side UI preferences, persisted in localStorage. Kept separate from auth
// tokens so they survive logout/login (a remembered view should outlast a session).

/** 'agendaList' is our own infinite-scrolling upcoming list, not a FullCalendar view. */
export type CalendarView = 'dayGridMonth' | 'timeGridWeek' | 'timeGridDay' | 'agendaList'

const VIEW_KEY = 'calendarit.view'
const DEFAULT_VIEW: CalendarView = 'dayGridMonth'
const KNOWN_VIEWS: readonly CalendarView[] = ['dayGridMonth', 'timeGridWeek', 'timeGridDay', 'agendaList']

/** Stand-in id for "events without a category" in the category visibility filter. */
export const UNCATEGORIZED = 'none'

/** The view to open the calendar in — the last one the user chose, or the month default. */
export function getSavedView(): CalendarView {
  const raw = localStorage.getItem(VIEW_KEY)
  if (raw === 'listMonth') return 'agendaList' // pre-agenda list view, remembered before it was replaced
  return KNOWN_VIEWS.includes(raw as CalendarView) ? (raw as CalendarView) : DEFAULT_VIEW
}

/** Remember the user's current calendar view for next time. */
export function saveView(view: string): void {
  if (KNOWN_VIEWS.includes(view as CalendarView)) {
    localStorage.setItem(VIEW_KEY, view)
  }
}

const VISIBLE_CALENDARS_KEY = 'calendarit.visibleCalendars'

/** Which calendars are shown in the calendar view. `null` = all of them. */
export function getVisibleCalendars(): string[] | null {
  const raw = localStorage.getItem(VISIBLE_CALENDARS_KEY)
  if (!raw) return null
  try {
    const parsed = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter((x) => typeof x === 'string') : null
  } catch {
    return null
  }
}

export function saveVisibleCalendars(ids: string[] | null): void {
  if (ids === null) localStorage.removeItem(VISIBLE_CALENDARS_KEY)
  else localStorage.setItem(VISIBLE_CALENDARS_KEY, JSON.stringify(ids))
}

const VISIBLE_CATEGORIES_KEY = 'calendarit.visibleCategories'

/** Which categories are shown. `null` = all; the id `'none'` stands for uncategorized. */
export function getVisibleCategories(): string[] | null {
  const raw = localStorage.getItem(VISIBLE_CATEGORIES_KEY)
  if (!raw) return null
  try {
    const parsed = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter((x) => typeof x === 'string') : null
  } catch {
    return null
  }
}

export function saveVisibleCategories(ids: string[] | null): void {
  if (ids === null) localStorage.removeItem(VISIBLE_CATEGORIES_KEY)
  else localStorage.setItem(VISIBLE_CATEGORIES_KEY, JSON.stringify(ids))
}
