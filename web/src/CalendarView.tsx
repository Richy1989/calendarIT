import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import FullCalendar from '@fullcalendar/react'
import dayGridPlugin from '@fullcalendar/daygrid'
import timeGridPlugin from '@fullcalendar/timegrid'
import interactionPlugin from '@fullcalendar/interaction'
import type { DateClickArg } from '@fullcalendar/interaction'
import type {
  DateSelectArg,
  DatesSetArg,
  DayCellMountArg,
  EventApi,
  EventMountArg,
  EventClickArg,
  EventChangeArg,
  EventInput,
} from '@fullcalendar/core'
import EventModal, { type EventDraft } from './EventModal'
import AgendaView from './AgendaView'
import { createEvent, deleteEvent, exportEventIcs, getEvent, listEvents, respondToInvitation, updateEvent, type EventDto, type RsvpStatus, type SaveEventRequest } from './api/events'
import { listCalendars } from './api/calendars'
import { listCategories } from './api/categories'
import { saveDefaultView } from './api/profile'
import { useHour12 } from './clock'
import { useFirstDay } from './weekStart'
import { Popover } from './components/Popover'
import { addDays, dayKey, toLocalInput } from './lib/dates'
import { useUndoStack } from './lib/useUndoStack'
import { getSavedView, saveView, UNCATEGORIZED } from './prefs'
import {
  hasEventClipboard,
  pasteOnto,
  readEventClipboard,
  snapshotFromDto,
  subscribeClipboard,
  writeEventClipboard,
  writeIcsToSystemClipboard,
} from './lib/eventClipboard'

// Uncategorized events render in this neutral default (categories carry the real colors).
const DEFAULT_COLOR = '#708090' // slategray

function hexToRgba(hex: string, alpha: number): string {
  const h = hex.replace('#', '')
  const full = h.length === 3 ? h.split('').map((c) => c + c).join('') : h
  const r = parseInt(full.slice(0, 2), 16)
  const g = parseInt(full.slice(2, 4), 16)
  const b = parseInt(full.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone

/** Tracks a CSS media query and re-renders when it flips. Used to swap the calendar's
 *  toolbar between the roomy desktop layout and a compact two-row phone layout. */
function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(
    () => typeof window !== 'undefined' && window.matchMedia(query).matches,
  )
  useEffect(() => {
    const mq = window.matchMedia(query)
    const onChange = () => setMatches(mq.matches)
    onChange()
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [query])
  return matches
}

// API DTO → FullCalendar input. Recurring occurrences get a unique render id but carry
// the master id (seriesId) for edit/delete, and are not drag-editable in this phase.
function dtoToInput(dto: EventDto): EventInput {
  const color = dto.color ?? DEFAULT_COLOR
  return {
    id: dto.recurring ? `${dto.id}__${dto.start}` : dto.id,
    title: dto.title,
    start: dto.allDay ? dto.start.slice(0, 10) : dto.start,
    // Stored all-day ends are inclusive; FullCalendar's are exclusive, so shift by a day
    // (otherwise multi-day all-day events render one day short).
    end: dto.end ? (dto.allDay ? addDays(dto.end.slice(0, 10), 1) : dto.end) : undefined,
    allDay: dto.allDay,
    editable: !dto.recurring,
    backgroundColor: hexToRgba(color, 0.18),
    borderColor: color,
    extendedProps: {
      seriesId: dto.id,
      recurring: dto.recurring,
      occurrenceStart: dto.start,
      categoryId: dto.categoryId ?? null,
      location: dto.location ?? '',
      description: dto.description ?? '',
      reminders: dto.reminders,
      invitationStatus: dto.invitationStatus ?? null,
      organizerEmail: dto.organizerEmail ?? null,
    },
  }
}

function toApiIso(local: string, allDay: boolean): string {
  return allDay
    ? new Date(`${local.slice(0, 10)}T00:00:00Z`).toISOString()
    : new Date(local).toISOString()
}

function draftToRequest(d: EventDraft): SaveEventRequest {
  return {
    calendarId: d.calendarId ?? null,
    title: d.title,
    description: d.description || null,
    location: d.location || null,
    categoryId: d.categoryId,
    start: toApiIso(d.start, d.allDay),
    end: d.end ? toApiIso(d.end, d.allDay) : null,
    allDay: d.allDay,
    recurrence: d.recurrence || null,
    timeZone: browserTz,
    reminders: d.reminders.map((r) => ({ minutesBefore: r.minutesBefore, channel: r.channel })),
    attendees: d.attendees.map((a) => ({ email: a.email, name: a.name ?? null })),
  }
}

// A server DTO → a save request, for recreating an event when a delete is undone. The DTO's
// start/end are already API-shaped ISO instants, so they pass straight through. (Exdates
// aren't carried on the DTO, so undoing the delete of a *recurring series* restores it
// without its previously-excluded occurrences — a rare, acknowledged edge.)
function dtoToRequest(dto: EventDto): SaveEventRequest {
  return {
    calendarId: dto.calendarId ?? null,
    title: dto.title,
    description: dto.description ?? null,
    location: dto.location ?? null,
    categoryId: dto.categoryId ?? null,
    start: dto.start,
    end: dto.end ?? null,
    allDay: dto.allDay,
    recurrence: dto.recurrence ?? null,
    timeZone: browserTz,
    reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
    attendees: dto.attendees.map((a) => ({ email: a.email, name: a.name ?? null })),
  }
}

type ContextMenu =
  | { kind: 'date'; x: number; y: number; date: Date }
  | { kind: 'event'; x: number; y: number; seriesId: string; recurring: boolean; occurrenceStart: string }

const DOUBLE_CLICK_MS = 350

export default function CalendarView({
  focus,
  serverView,
  visibleCalendarIds,
  onChangeVisible,
  onManage,
  visibleCategoryIds,
  onChangeVisibleCategories,
  onManageCategories,
}: {
  focus?: { date: string; n: number } | null
  serverView?: string | null
  /** Calendars to show; null/undefined = all of them. */
  visibleCalendarIds?: string[] | null
  /** Called when the user toggles calendar visibility in the toolbar picker. */
  onChangeVisible?: (ids: string[] | null) => void
  /** Opens Settings → Calendars ("Manage calendars…"). */
  onManage?: () => void
  /** Categories to show; null/undefined = all (incl. uncategorized, see UNCATEGORIZED). */
  visibleCategoryIds?: string[] | null
  /** Called when the user toggles category visibility in the toolbar picker. */
  onChangeVisibleCategories?: (ids: string[] | null) => void
  /** Opens Settings → Categories ("Manage categories…"). */
  onManageCategories?: () => void
}) {
  const queryClient = useQueryClient()
  // "list" is our own agenda panel, not a FullCalendar view: while active, FullCalendar
  // stays mounted (hidden) so its date/view state survives the round trip.
  const savedView = getSavedView()
  const [agendaMode, setAgendaMode] = useState(savedView === 'agendaList')
  // Below this width the toolbar can't hold every control on one row, so we split it
  // across a header (New · title · prev/next/today) and a footer (pickers + view switch).
  const isNarrow = useMediaQuery('(max-width: 640px)')
  const initialFcView = savedView === 'agendaList' ? 'dayGridMonth' : savedView
  const [range, setRange] = useState<{ from: string; to: string } | null>(null)
  const { data: dtos = [] } = useQuery({
    queryKey: ['events', range?.from, range?.to],
    queryFn: () => listEvents(range!.from, range!.to),
    enabled: !!range,
  })
  const { data: calendars = [] } = useQuery({ queryKey: ['calendars'], queryFn: listCalendars })
  const { data: categories = [], isSuccess: categoriesLoaded } = useQuery({ queryKey: ['categories'], queryFn: listCategories })

  // The persisted category filter may reference categories deleted since (their events
  // are now uncategorized). Drop stale ids — and when the filter pointed only at deleted
  // categories, fall back to "all" instead of a mysteriously empty calendar. A
  // deliberately empty selection ([]) is preserved.
  const effectiveCategoryIds = useMemo(() => {
    if (!visibleCategoryIds || !categoriesLoaded) return visibleCategoryIds ?? null
    const known = new Set([...categories.map((c) => c.id), UNCATEGORIZED])
    const pruned = visibleCategoryIds.filter((id) => known.has(id))
    if (pruned.length === 0 && visibleCategoryIds.length > 0) return null
    return pruned
  }, [visibleCategoryIds, categories, categoriesLoaded])

  const events = useMemo(
    () =>
      dtos
        .filter((d) => d.invitationStatus !== 'Declined') // a declined invitation drops off the calendar
        .filter((d) => !visibleCalendarIds || visibleCalendarIds.includes(d.calendarId))
        .filter((d) => !effectiveCategoryIds || effectiveCategoryIds.includes(d.categoryId ?? UNCATEGORIZED))
        .map(dtoToInput),
    [dtos, visibleCalendarIds, effectiveCategoryIds],
  )

  // Open week/day scrolled to roughly the current time (with ~1.5h of lead-in above it), so the
  // live "now" indicator line is visible on load instead of hidden below the default 6am scroll —
  // the way Google/Apple calendars behave. Computed once on mount; the line itself keeps updating.
  const scrollTime = useMemo(() => {
    const now = new Date()
    const minutes = Math.max(0, now.getHours() * 60 + now.getMinutes() - 90)
    const hh = String(Math.floor(minutes / 60)).padStart(2, '0')
    const mm = String(minutes % 60).padStart(2, '0')
    return `${hh}:${mm}:00`
  }, [])

  // New events land in the first visible calendar (or the first one overall).
  const defaultCalendarId = () =>
    calendars.find((c) => !visibleCalendarIds || visibleCalendarIds.includes(c.id))?.id ?? calendars[0]?.id

  // Calendar visibility picker, anchored to its toolbar button.
  const [calPop, setCalPop] = useState<{ x: number; y: number } | null>(null)
  const isCalVisible = (id: string) => !visibleCalendarIds || visibleCalendarIds.includes(id)
  const shownCount = calendars.filter((c) => isCalVisible(c.id)).length
  const calPickerLabel =
    calendars.length <= 1
      ? (calendars[0]?.name ?? 'Calendars')
      : shownCount === calendars.length
        ? 'All calendars ▾'
        : shownCount === 1
          ? `${calendars.find((c) => isCalVisible(c.id))?.name} ▾`
          : `${shownCount} of ${calendars.length} ▾`

  // Deselecting down to nothing is allowed — an empty array means "show nothing",
  // while null keeps meaning "all". The "All" row toggles between the two.
  const toggleCal = (id: string) => {
    const next = calendars.filter((c) => (c.id === id ? !isCalVisible(c.id) : isCalVisible(c.id))).map((c) => c.id)
    onChangeVisible?.(next.length === calendars.length ? null : next)
  }

  const toggleAllCals = () => onChangeVisible?.(shownCount === calendars.length ? [] : null)

  // Category visibility picker — same pattern as the calendar picker; the extra
  // UNCATEGORIZED entry covers events without a category.
  const [catPop, setCatPop] = useState<{ x: number; y: number } | null>(null)
  const allCatIds = [...categories.map((c) => c.id), UNCATEGORIZED]
  const isCatVisible = (id: string) => !effectiveCategoryIds || effectiveCategoryIds.includes(id)
  const shownCatCount = allCatIds.filter(isCatVisible).length
  const catPickerLabel =
    categories.length === 0
      ? 'Categories ▾'
      : shownCatCount === allCatIds.length
        ? 'All categories ▾'
        : shownCatCount === 1
          ? `${categories.find((c) => isCatVisible(c.id))?.name ?? 'Uncategorized'} ▾`
          : `${shownCatCount} of ${allCatIds.length} ▾`

  // Same contract as the calendars: empty array = nothing, null = all.
  const toggleCat = (id: string) => {
    const next = allCatIds.filter((x) => (x === id ? !isCatVisible(x) : isCatVisible(x)))
    onChangeVisibleCategories?.(next.length === allCatIds.length ? null : next)
  }

  const toggleAllCats = () => onChangeVisibleCategories?.(shownCatCount === allCatIds.length ? [] : null)

  const hour12 = useHour12()
  const firstDay = useFirstDay()
  const [draft, setDraft] = useState<EventDraft | null>(null)

  // Ctrl-Z / Ctrl-Shift-Z. `record` gets an inverse for each change; the toast names the
  // step so walking the stack isn't blind. `originalEdit` snapshots an event as it was
  // opened, so saving an edit can capture the state to restore.
  const { record, undo, redo } = useUndoStack()
  const originalEdit = useRef<EventDraft | null>(null)
  const [toast, setToast] = useState<string | null>(null)
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const showToast = useCallback((msg: string) => {
    setToast(msg)
    if (toastTimer.current) clearTimeout(toastTimer.current)
    toastTimer.current = setTimeout(() => setToast(null), 2500)
  }, [])
  const [menu, setMenu] = useState<ContextMenu | null>(null)
  // Whether the clipboard holds a copied event — drives the "Paste" affordance, and stays in
  // sync when another tab copies (subscribeClipboard also listens for the storage event).
  const [canPaste, setCanPaste] = useState(hasEventClipboard)
  useEffect(() => subscribeClipboard(() => setCanPaste(hasEventClipboard())), [])
  // Year quick-jump popover, opened by clicking the toolbar title ("August 2026").
  // `base` is the first year of the visible 12-year window; `current` the year on screen.
  const [yearPop, setYearPop] = useState<{ x: number; y: number; base: number; current: number } | null>(null)
  // The day the grid is "on". Never empty: a fresh page starts on today, so switching to week
  // or day view lands on today rather than on whichever date the month grid happened to be
  // anchored to after a few pages of browsing.
  const [selectedDate, setSelectedDate] = useState<string>(() => dayKey(new Date()))
  // A single click selects (highlights) an appointment; a double click opens it. This selection is
  // what the keyboard copy/cut act on. We keep the render id (to re-apply the highlight after paging
  // re-mounts the element), the master series id (for copy/cut), and the live DOM element so the
  // highlight can move without a full React re-render.
  const [selectedEvent, setSelectedEvent] = useState<{ id: string; seriesId: string } | null>(null)
  const selectedEventIdRef = useRef<string | null>(null)
  // A single event can render as several DOM segments (a multi-week bar in month view, or a
  // multi-day event split across day columns). We track every mounted segment by render id so the
  // whole appointment highlights as one, not just the piece that was clicked.
  const eventSegs = useRef(new Map<string, Set<HTMLElement>>())
  const lastClick = useRef<{ dateStr: string; time: number } | null>(null)
  const calendarRef = useRef<FullCalendar>(null)
  const fcHostRef = useRef<HTMLDivElement>(null)
  const lastView = useRef<string | null>(null)
  const appliedServerView = useRef(false)
  const suppressPersist = useRef(false)

  // Keep the events query range in sync, and — when the *view* changes (month→week→day)
  // rather than just paging — reveal the week/day that holds the currently selected cell.
  const handleDatesSet = (arg: DatesSetArg) => {
    setRange({ from: arg.start.toISOString(), to: arg.end.toISOString() })

    const viewChanged = lastView.current !== null && lastView.current !== arg.view.type
    lastView.current = arg.view.type
    if (viewChanged) {
      saveView(arg.view.type) // fast local cache: restores instantly with no flash next load
      if (suppressPersist.current) {
        suppressPersist.current = false // this change came from applying the server value
      } else {
        saveDefaultView(arg.view.type).catch(() => {}) // fire-and-forget: remember per-user in the DB
      }
    }
    if (!viewChanged) return

    const sel = new Date(`${selectedDate}T00:00:00`)
    if (sel < arg.start || sel >= arg.end) {
      // gotoDate re-fires datesSet, but with the view unchanged now, so it won't recurse.
      calendarRef.current?.getApi().gotoDate(sel)
    }
  }

  // The DB-remembered view (from the user's profile) is the cross-device source of truth.
  // Apply it once when it arrives; the local cache already gave us an instant initial view,
  // so this only does anything when another device changed the preference.
  useEffect(() => {
    if (appliedServerView.current || !serverView) return
    appliedServerView.current = true
    if (serverView === 'agendaList' || serverView === 'listMonth') {
      setAgendaMode(true)
      saveView('agendaList')
      return
    }
    const api = calendarRef.current?.getApi()
    if (!api || api.view.type === serverView) return
    suppressPersist.current = true // don't PUT back a value we just read from the server
    api.changeView(serverView)
    saveView(serverView)
  }, [serverView])

  // Entering / leaving the agenda list, persisted like any other view choice.
  const enterAgenda = () => {
    setAgendaMode(true)
    saveView('agendaList')
    saveDefaultView('agendaList').catch(() => {})
  }

  const exitAgenda = (view: 'dayGridMonth' | 'timeGridWeek' | 'timeGridDay') => {
    setAgendaMode(false)
    const api = calendarRef.current?.getApi()
    if (api && api.view.type !== view) {
      api.changeView(view) // datesSet fires and persists the choice
    } else {
      saveView(view)
      saveDefaultView(view).catch(() => {})
    }
    // FullCalendar was display:none while the list was up; re-measure once visible.
    requestAnimationFrame(() => calendarRef.current?.getApi().updateSize())
  }

  // A search pick (from the header) jumps the calendar to that appointment's day view.
  useEffect(() => {
    if (!focus) return
    const api = calendarRef.current?.getApi()
    if (!api) return
    setAgendaMode(false) // a date jump is a grid concern; leave the list if it's up
    const d = new Date(focus.date)
    api.changeView('timeGridDay', d)
    setSelectedDate(dayKey(d))
  }, [focus])

  // Left/Right arrow keys page the calendar (prev/next), matching the toolbar buttons.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
      if (draft || menu || yearPop || calPop || catPop) return // don't steal keys from the editor / popovers
      if (agendaMode) return // the list has no prev/next pages
      if (e.metaKey || e.ctrlKey || e.altKey) return
      const target = e.target as HTMLElement | null
      const tag = target?.tagName
      if (target?.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return
      const api = calendarRef.current?.getApi()
      if (!api) return
      e.preventDefault()
      if (e.key === 'ArrowRight') api.next()
      else api.prev()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [draft, menu, yearPop, calPop, catPop, agendaMode])

  // Touch swipe: on phones/tablets a horizontal flick over the grid pages prev/next, the same
  // as the toolbar arrows and the desktop Left/Right keys. We never preventDefault, so vertical
  // scrolling in week/day views is untouched — only a clearly-horizontal, quick flick pages.
  useEffect(() => {
    const host = fcHostRef.current
    if (!host) return

    let startX = 0
    let startY = 0
    let startT = 0
    let tracking = false

    const onStart = (e: TouchEvent) => {
      if (e.touches.length !== 1) { tracking = false; return } // ignore pinch / multi-touch
      startX = e.touches[0].clientX
      startY = e.touches[0].clientY
      startT = Date.now()
      tracking = true
    }
    const onMove = (e: TouchEvent) => {
      if (e.touches.length > 1) tracking = false // became a pinch mid-gesture
    }
    const onEnd = (e: TouchEvent) => {
      if (!tracking) return
      tracking = false
      if (agendaMode || draft) return // the list has no pages; don't page behind the editor
      const t = e.changedTouches[0]
      const dx = t.clientX - startX
      const dy = t.clientY - startY
      if (Date.now() - startT > 600) return // slow = a scroll or long-press drag, not a flick
      if (Math.abs(dx) < 60) return // too short to be a deliberate swipe
      if (Math.abs(dx) < Math.abs(dy) * 1.5) return // too vertical — leave scrolling alone
      const api = calendarRef.current?.getApi()
      if (!api) return
      if (dx < 0) api.next() // swipe left → forward in time
      else api.prev() // swipe right → back
    }

    host.addEventListener('touchstart', onStart, { passive: true })
    host.addEventListener('touchmove', onMove, { passive: true })
    host.addEventListener('touchend', onEnd, { passive: true })
    return () => {
      host.removeEventListener('touchstart', onStart)
      host.removeEventListener('touchmove', onMove)
      host.removeEventListener('touchend', onEnd)
    }
  }, [agendaMode, draft])

  // Popover openers, shared by the FullCalendar toolbar buttons and the agenda toolbar.
  const openCalPop = (rect: DOMRect) => {
    setCatPop(null)
    setCalPop((p) => (p ? null : { x: rect.left, y: rect.bottom + 6 }))
  }
  const openCatPop = (rect: DOMRect) => {
    setCalPop(null)
    setCatPop((p) => (p ? null : { x: rect.left, y: rect.bottom + 6 }))
  }

  // calPop/catPop dismissal (Esc / scroll / resize / outside-click) is handled by <Popover>.

  // Clicking the toolbar title ("August 2026") opens a small popover to jump years.
  // FullCalendar owns the toolbar DOM, so the listener is delegated from the document.
  useEffect(() => {
    const onClick = (e: MouseEvent) => {
      const title = (e.target as HTMLElement | null)?.closest?.('.fc-toolbar-title')
      if (!title) return
      const rect = title.getBoundingClientRect()
      const year = (calendarRef.current?.getApi().getDate() ?? new Date()).getFullYear()
      setYearPop({ x: rect.left + rect.width / 2, y: rect.bottom + 6, base: year - 5, current: year })
    }
    document.addEventListener('click', onClick)
    return () => document.removeEventListener('click', onClick)
  }, [])

  // yearPop dismissal is handled by <Popover>.

  // Jump to the same date/view in another year.
  const pickYear = (year: number) => {
    const api = calendarRef.current?.getApi()
    if (api) {
      const d = api.getDate()
      d.setFullYear(year)
      api.gotoDate(d)
    }
    setYearPop(null)
  }

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['events'] })
  const createMut = useMutation({ mutationFn: createEvent, onSuccess: invalidate })
  const updateMut = useMutation({
    mutationFn: (v: { id: string; body: SaveEventRequest }) => updateEvent(v.id, v.body),
    onSuccess: invalidate,
  })
  const deleteMut = useMutation({
    mutationFn: (v: { id: string; occurrence?: string }) => deleteEvent(v.id, v.occurrence),
    onSuccess: invalidate,
  })
  const rsvpMut = useMutation({
    mutationFn: (v: { id: string; status: RsvpStatus }) => respondToInvitation(v.id, v.status),
    onSuccess: invalidate,
  })

  const doUndo = useCallback(async () => {
    try {
      const label = await undo()
      if (label) showToast(`Undid: ${label}`)
    } catch {
      showToast('Couldn’t undo that')
    }
  }, [undo, showToast])
  const doRedo = useCallback(async () => {
    try {
      const label = await redo()
      if (label) showToast(`Redid: ${label}`)
    } catch {
      showToast('Couldn’t redo that')
    }
  }, [redo, showToast])

  // Ctrl-Z steps back, Ctrl-Shift-Z / Ctrl-Y forward. Ignored while the editor is open or a
  // text field is focused, so it never hijacks the browser's own undo inside an input.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return
      const key = e.key.toLowerCase()
      const isUndo = key === 'z' && !e.shiftKey
      const isRedo = (key === 'z' && e.shiftKey) || key === 'y'
      if (!isUndo && !isRedo) return
      if (draft) return // modal open: leave Ctrl-Z to the focused field's native text undo
      const target = e.target as HTMLElement | null
      const tag = target?.tagName
      if (target?.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return
      e.preventDefault()
      void (isUndo ? doUndo() : doRedo())
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [draft, doUndo, doRedo])

  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setMenu(null)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
      window.removeEventListener('keydown', onKey)
    }
  }, [menu])

  // Custom "today" button: besides jumping the view (the built-in behavior), it also
  // selects/highlights today's cell — same effect as clicking that day. A custom button
  // is also never auto-disabled, so the highlight works even when today is already shown.
  const goToday = () => {
    calendarRef.current?.getApi().today()
    setSelectedDate(dayKey(new Date()))
  }

  const openNew = () => {
    const start = new Date()
    start.setMinutes(0, 0, 0)
    start.setHours(start.getHours() + 1)
    openNewOn(start, false)
  }

  // New events start in the first category (so they get a color without extra clicks).
  const defaultCategoryId = () => categories[0]?.id ?? null

  const openNewOn = (date: Date, allDay: boolean) => {
    const blank = { title: '', categoryId: defaultCategoryId(), location: '', description: '', recurrence: '', reminders: [], attendees: [], calendarId: defaultCalendarId() }
    if (allDay) {
      const day = toLocalInput(date).slice(0, 10)
      setDraft({ ...blank, start: day, end: day, allDay: true })
      return
    }
    const start = new Date(date)
    if (start.getHours() === 0 && start.getMinutes() === 0) start.setHours(9)
    const end = new Date(start.getTime() + 60 * 60 * 1000)
    setDraft({ ...blank, start: toLocalInput(start), end: toLocalInput(end), allDay: false })
  }

  // Drag-selection (mouse, as in any calendar): day view picks a time range in hours,
  // week view a start/end time possibly spanning days, month view a span of days.
  // Opens the new-appointment editor prefilled with exactly what was selected.
  const handleSelect = (arg: DateSelectArg) => {
    clearEventSelection()
    setSelectedDate(dayKey(arg.start))
    const blank = { title: '', categoryId: defaultCategoryId(), location: '', description: '', recurrence: '', reminders: [], attendees: [], calendarId: defaultCalendarId() }
    if (arg.allDay) {
      const startDay = dayKey(arg.start)
      const endDay = addDays(dayKey(arg.end), -1) // exclusive → inclusive last day
      setDraft({ ...blank, start: startDay, end: endDay < startDay ? startDay : endDay, allDay: true })
    } else {
      setDraft({ ...blank, start: toLocalInput(arg.start), end: toLocalInput(arg.end), allDay: false })
    }
  }

  // Closes the editor and clears any pending drag-selection highlight behind it.
  const closeDraft = () => {
    setDraft(null)
    calendarRef.current?.getApi().unselect()
  }

  // Fine-pointer (mouse) devices get select-on-single-click / open-on-double-click. Touch has no
  // easy double-tap and no keyboard shortcuts, so a single tap there keeps opening the editor.
  const isCoarsePointer = () => window.matchMedia?.('(pointer: coarse)').matches ?? false

  // Toggle the highlight across every mounted segment of one event (all its week-rows / columns).
  const markSegments = (id: string, on: boolean) => {
    eventSegs.current.get(id)?.forEach((el) => el.classList.toggle('fc-event-selected', on))
  }

  const clearEventSelection = () => {
    if (selectedEventIdRef.current) markSegments(selectedEventIdRef.current, false)
    selectedEventIdRef.current = null
    setSelectedEvent(null)
  }

  // Highlight the clicked appointment (all of its segments), moving the highlight off the previous.
  const selectEvent = (ev: EventApi) => {
    if (selectedEventIdRef.current && selectedEventIdRef.current !== ev.id) {
      markSegments(selectedEventIdRef.current, false)
    }
    selectedEventIdRef.current = ev.id
    markSegments(ev.id, true)
    setSelectedEvent({ id: ev.id, seriesId: ev.extendedProps.seriesId as string })
  }

  const handleEventClick = (info: EventClickArg) => {
    if (isCoarsePointer()) {
      openForEdit(info.event.extendedProps.seriesId) // touch: a tap opens, as before
      return
    }
    setSelectedDate(dayKey(info.event.start ?? new Date()))
    selectEvent(info.event)
  }

  const handleDateClick = (arg: DateClickArg) => {
    clearEventSelection() // clicking a day drops the appointment selection
    setSelectedDate(dayKey(arg.date))
    const now = Date.now()
    const prev = lastClick.current
    if (prev && prev.dateStr === arg.dateStr && now - prev.time < DOUBLE_CLICK_MS) {
      lastClick.current = null
      openNewOn(arg.date, false)
    } else {
      lastClick.current = { dateStr: arg.dateStr, time: now }
    }
  }

  // Always load the master (unexpanded) event so editing a recurring occurrence edits the series.
  const openForEdit = async (masterId: string) => {
    const dto = await getEvent(masterId)
    const allDay = dto.allDay
    const startLocal = allDay ? dto.start.slice(0, 10) : toLocalInput(new Date(dto.start))
    const endLocal = dto.end ? (allDay ? dto.end.slice(0, 10) : toLocalInput(new Date(dto.end))) : startLocal
    const loaded: EventDraft = {
      id: dto.id,
      calendarId: dto.calendarId,
      title: dto.title,
      allDay,
      start: startLocal,
      end: endLocal,
      categoryId: dto.categoryId ?? null,
      location: dto.location ?? '',
      description: dto.description ?? '',
      recurrence: dto.recurrence ?? '',
      reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
      attendees: dto.attendees.map((a) => ({ email: a.email, name: a.name, status: a.status })),
      invitationStatus: dto.invitationStatus ?? null,
      organizerEmail: dto.organizerEmail ?? null,
    }
    originalEdit.current = loaded // snapshot: an edit's undo restores this state
    setDraft(loaded)
  }

  const respond = (id: string, status: RsvpStatus) => {
    rsvpMut.mutate({ id, status })
    closeDraft()
  }

  const save = (d: EventDraft) => {
    const body = draftToRequest(d)
    const label = d.title.trim() || 'event'
    if (d.id) {
      const id = d.id
      // The state as it was opened — restore it on undo (only when it's the event we edited).
      const prev = originalEdit.current
      const prevBody = prev && prev.id === id ? draftToRequest(prev) : null
      updateMut.mutate(
        { id, body },
        {
          onSuccess: () => {
            if (!prevBody) return
            record({
              label: `edited “${label}”`,
              undo: () => updateMut.mutateAsync({ id, body: prevBody }),
              redo: () => updateMut.mutateAsync({ id, body }),
            })
          },
        },
      )
    } else {
      createMut.mutate(body, {
        // Undo deletes what we just made; redo re-creates it. A recreate mints a new id, so the
        // live id is tracked in the closure for a later undo to delete the right row.
        onSuccess: (created) => {
          let liveId = created.id
          record({
            label: `created “${label}”`,
            undo: () => deleteMut.mutateAsync({ id: liveId }),
            redo: async () => { liveId = (await createMut.mutateAsync(body)).id },
          })
        },
      })
    }
    closeDraft()
  }

  const remove = async (id: string, occurrence?: string) => {
    // Deleting one occurrence of a series only adds an EXDATE, which no endpoint can lift —
    // so it isn't undoable and stays off the stack. Whole-event deletes are recreated on undo.
    if (occurrence) {
      deleteMut.mutate({ id, occurrence })
      closeDraft()
      return
    }
    // Capture the event before it's gone, so undo can recreate it.
    const snapshot = await getEvent(id).catch(() => null)
    deleteMut.mutate(
      { id },
      {
        onSuccess: () => {
          if (!snapshot) return
          const body = dtoToRequest(snapshot)
          let liveId: string | null = null // set by undo's recreate; redo deletes that new row
          record({
            label: `deleted “${snapshot.title || 'event'}”`,
            undo: async () => { liveId = (await createMut.mutateAsync(body)).id },
            redo: () => deleteMut.mutateAsync({ id: liveId ?? id }),
          })
        },
      },
    )
    closeDraft()
  }

  // Copy an event to the clipboard. Always copies the master series (so copying a recurring
  // occurrence duplicates the whole rule, like "Edit series"). Writes the reliable in-app JSON
  // snapshot, then best-effort iCalendar to the system clipboard for other calendar apps.
  const copyEvent = async (masterId: string) => {
    const dto = await getEvent(masterId).catch(() => null)
    if (!dto) { showToast('Couldn’t copy that'); return }
    writeEventClipboard(snapshotFromDto(dto))
    showToast(`Copied “${dto.title || 'event'}”`)
    void writeIcsToSystemClipboard(() => exportEventIcs(masterId))
  }

  // Cut: copy the event to the clipboard, then delete it (undoable — Ctrl-Z brings it back).
  // Always the whole series, matching Copy; Paste (Ctrl-V) re-creates it on the target day. No
  // draft is open here, so this reuses the same undoable delete path as the context menu.
  const cutEvent = async (masterId: string) => {
    const dto = await getEvent(masterId).catch(() => null)
    if (!dto) { showToast('Couldn’t cut that'); return }
    writeEventClipboard(snapshotFromDto(dto))
    void writeIcsToSystemClipboard(() => exportEventIcs(masterId))
    clearEventSelection()
    await remove(masterId)
    showToast(`Cut “${dto.title || 'event'}”`)
  }

  // Paste the clipboard event onto a day, preserving its time-of-day and duration. Creates a
  // fresh event (new server UID) via the same create + undo path a new appointment uses.
  const doPaste = (targetDay: string) => {
    const snap = readEventClipboard()
    if (!snap) return
    const draft = pasteOnto(snap, targetDay)
    // The copied calendar may be hidden, deleted, or from another tab — fall back to the default.
    if (!draft.calendarId || !calendars.some((c) => c.id === draft.calendarId)) draft.calendarId = defaultCalendarId()
    const body = draftToRequest(draft)
    const label = draft.title.trim() || 'event'
    createMut.mutate(body, {
      onSuccess: (created) => {
        let liveId = created.id
        record({
          label: `pasted “${label}”`,
          undo: () => deleteMut.mutateAsync({ id: liveId }),
          redo: async () => { liveId = (await createMut.mutateAsync(body)).id },
        })
      },
    })
    showToast(`Pasted “${label}”`)
  }

  // Ctrl/Cmd-V pastes onto the selected day. The ref keeps the listener stable while always
  // calling the latest doPaste (which closes over calendars + mutations).
  const pasteRef = useRef(doPaste)
  pasteRef.current = doPaste
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return
      if (e.key.toLowerCase() !== 'v') return
      if (draft || menu || yearPop || calPop || catPop) return // don't hijack paste in the editor / popovers
      const target = e.target as HTMLElement | null
      const tag = target?.tagName
      if (target?.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return // leave native paste alone
      if (!hasEventClipboard()) return
      e.preventDefault()
      pasteRef.current(selectedDate)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [draft, menu, yearPop, calPop, catPop, selectedDate])

  // Keyboard actions on the selected appointment: Ctrl/Cmd-C copies, Ctrl/Cmd-X cuts,
  // Delete/Backspace removes it (all undoable), Escape clears the selection. Refs keep the
  // listener stable while always calling the latest copy/cut/remove (which close over mutations
  // + calendars). Guarded like paste: ignored in the editor, popovers, or a text field, and
  // Ctrl-C is left to the browser while the user has real page text selected to copy.
  const copyRef = useRef(copyEvent)
  copyRef.current = copyEvent
  const cutRef = useRef(cutEvent)
  cutRef.current = cutEvent
  const removeSelRef = useRef((id: string) => remove(id))
  removeSelRef.current = (id: string) => remove(id)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (draft || menu || yearPop || calPop || catPop) return
      if (e.key === 'Escape') { clearEventSelection(); return }
      const target = e.target as HTMLElement | null
      const tag = target?.tagName
      if (target?.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return

      // Delete / Backspace removes the selected appointment (whole series, like Cut; undoable).
      if (e.key === 'Delete' || e.key === 'Backspace') {
        if (!selectedEvent) return
        e.preventDefault()
        const id = selectedEvent.seriesId
        clearEventSelection()
        void removeSelRef.current(id)
        return
      }

      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return
      const key = e.key.toLowerCase()
      if (key !== 'c' && key !== 'x') return
      if (!selectedEvent) return
      if (key === 'c' && (window.getSelection()?.toString().length ?? 0) > 0) return // real text copy
      e.preventDefault()
      if (key === 'c') void copyRef.current(selectedEvent.seriesId)
      else void cutRef.current(selectedEvent.seriesId)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [draft, menu, yearPop, calPop, catPop, selectedEvent])

  // Fires after a drag or an edge-resize, for single events only (recurring occurrences are
  // not drag-editable). Sends just the fields a grid edit can touch: the API leaves attendees
  // and the owning calendar alone when they're absent, so guests survive a resize untouched.
  // A grid event (its current or pre-drag snapshot) → the save request a move/resize sends.
  const eventToBody = (ev: EventApi): SaveEventRequest => ({
    title: ev.title,
    description: (ev.extendedProps.description as string) || null,
    location: (ev.extendedProps.location as string) || null,
    categoryId: (ev.extendedProps.categoryId as string | null) ?? null, // keep the assignment on drag edits
    start: ev.allDay ? toApiIso(ev.startStr, true) : (ev.start ?? new Date()).toISOString(),
    // FullCalendar's all-day end is exclusive; the API stores an inclusive last day.
    end: ev.end ? (ev.allDay ? toApiIso(addDays(ev.endStr.slice(0, 10), -1), true) : ev.end.toISOString()) : null,
    allDay: ev.allDay,
    recurrence: null,
    timeZone: browserTz,
    reminders: (ev.extendedProps.reminders as { minutesBefore: number; channel: string }[] | undefined)?.map((r) => ({
      minutesBefore: r.minutesBefore,
      channel: r.channel,
    })) ?? null,
  })

  const applyChange = (info: EventChangeArg) => {
    const id = info.event.extendedProps.seriesId as string
    const newBody = eventToBody(info.event)
    const oldBody = eventToBody(info.oldEvent) // position before the drag — the undo target
    updateMut.mutate(
      { id, body: newBody },
      {
        // The grid already moved the box optimistically. If the save fails, put it back —
        // otherwise the calendar keeps showing times that were never stored.
        onError: () => info.revert(),
        // Record only once the move actually persisted (a reverted move isn't on the stack).
        onSuccess: () =>
          record({
            label: `moved “${info.event.title || 'event'}”`,
            undo: () => updateMut.mutateAsync({ id, body: oldBody }),
            redo: () => updateMut.mutateAsync({ id, body: newBody }),
          }),
      },
    )
  }

  const onDateCellMount = (arg: DayCellMountArg) => {
    arg.el.addEventListener('contextmenu', (e) => {
      e.preventDefault()
      setMenu({ kind: 'date', x: e.clientX, y: e.clientY, date: arg.date })
    })
  }

  const onEventMount = (arg: EventMountArg) => {
    // Expose the event's own category color to CSS, so the selection highlight glows in the
    // appointment's colour rather than a fixed accent (cyan already means "now"/"today").
    if (arg.event.borderColor) arg.el.style.setProperty('--ev-color', arg.event.borderColor)
    // Register this segment so a multi-segment appointment highlights as a whole (see eventSegs).
    const segs = eventSegs.current.get(arg.event.id) ?? new Set<HTMLElement>()
    segs.add(arg.el)
    eventSegs.current.set(arg.event.id, segs)
    // A received invitation reads by its RSVP state: pending & maybe stay provisional (dashed +
    // ✉/?), accepted becomes a solid confirmed box (✓). Declined ones are filtered out upstream
    // and never mount. The organizer is surfaced in the tooltip.
    const invite = arg.event.extendedProps.invitationStatus as string | null
    if (invite && invite !== 'Declined') {
      arg.el.classList.add('fc-invite')
      arg.el.classList.add(
        invite === 'Accepted' ? 'fc-invite-accepted' : invite === 'Tentative' ? 'fc-invite-tentative' : 'fc-invite-pending',
      )
      const organizer = arg.event.extendedProps.organizerEmail as string | null
      if (organizer) {
        arg.el.title = `Invitation from ${organizer}`
      }
    }
    // Re-apply the selection highlight when a segment (re)mounts (paging, data refresh, etc.).
    if (arg.event.id === selectedEventIdRef.current) arg.el.classList.add('fc-event-selected')
    // Double click opens the editor (a single click only selects — see handleEventClick).
    arg.el.addEventListener('dblclick', (e) => {
      e.preventDefault()
      openForEdit(arg.event.extendedProps.seriesId)
    })
    arg.el.addEventListener('contextmenu', (e) => {
      e.preventDefault()
      e.stopPropagation()
      setMenu({
        kind: 'event',
        x: e.clientX,
        y: e.clientY,
        seriesId: arg.event.extendedProps.seriesId,
        recurring: arg.event.extendedProps.recurring,
        occurrenceStart: arg.event.extendedProps.occurrenceStart,
      })
    })
  }

  // Drop a segment from the registry when FullCalendar unmounts it, so the id→segments map
  // doesn't leak stale elements across paging or data refreshes.
  const onEventUnmount = (arg: EventMountArg) => {
    const segs = eventSegs.current.get(arg.event.id)
    if (!segs) return
    segs.delete(arg.el)
    if (segs.size === 0) eventSegs.current.delete(arg.event.id)
  }

  return (
    <>
      <div className="calendar-fc-host" ref={fcHostRef} style={agendaMode ? { display: 'none' } : undefined}>
      <FullCalendar
        ref={calendarRef}
        plugins={[dayGridPlugin, timeGridPlugin, interactionPlugin]}
        initialView={initialFcView}
        customButtons={{
          addEvent: { text: '+  New', click: openNew },
          todaySelect: { text: 'today', click: goToday },
          calPicker: {
            text: calPickerLabel,
            click: (_ev, element) => openCalPop(element.getBoundingClientRect()),
          },
          catPicker: {
            text: catPickerLabel,
            click: (_ev, element) => openCatPop(element.getBoundingClientRect()),
          },
          listBtn: { text: 'list', click: enterAgenda },
        }}
        headerToolbar={
          isNarrow
            ? { left: 'addEvent', center: 'title', right: 'prev,next todaySelect' }
            : {
                left: 'addEvent calPicker catPicker',
                center: 'title',
                right: 'prev,next todaySelect dayGridMonth,timeGridWeek,timeGridDay,listBtn',
              }
        }
        // On phones the pickers + view switch move to a bottom bar — thumb-reachable and
        // it frees the top row for the date. Desktop keeps everything in the header.
        footerToolbar={
          isNarrow
            ? { left: 'calPicker catPicker', right: 'dayGridMonth,timeGridWeek,timeGridDay,listBtn' }
            : undefined
        }
        height="100%"
        nowIndicator
        // Which column month/week grids open on. Without this FullCalendar uses its own default
        // (Sunday), which disagreed with the Monday-first date picker.
        firstDay={firstDay}
        // Event-pill times and the week/day time axis follow the user's 12h/24h preference.
        eventTimeFormat={{ hour: 'numeric', minute: '2-digit', hour12 }}
        slotLabelFormat={{ hour: 'numeric', minute: '2-digit', hour12 }}
        // An en dash between the two ends of a range — the same separator the list view uses.
        defaultRangeSeparator=" – "
        scrollTime={scrollTime}
        scrollTimeReset={false}
        slotEventOverlap={false}
        editable
        // `editable` already allows dragging the *end* of an appointment; this adds the other
        // handle, so either border sets its time — the way every desktop calendar behaves.
        eventResizableFromStart
        // Resizing and dragging land on quarter hours. The visible lines stay half-hourly
        // (slotDuration), so this buys precision without making the grid busier.
        snapDuration="00:15:00"
        selectable
        selectMirror
        selectMinDistance={8} // plain clicks keep their click/double-click behavior; only a real drag selects
        unselectAuto={false} // the highlight stays under the editor; closeDraft() clears it
        dayMaxEvents={isNarrow ? 2 : 4}
        events={events}
        datesSet={handleDatesSet}
        dateClick={handleDateClick}
        select={handleSelect}
        // Which day is selected only means something where there are other days to tell it
        // apart from. Day view is that one day, so the highlight would just ring the whole
        // grid — and in cyan, competing with the now-indicator.
        dayCellClassNames={(arg) =>
          arg.view.type !== 'timeGridDay' && dayKey(arg.date) === selectedDate ? ['is-selected'] : []
        }
        eventClick={handleEventClick}
        eventChange={applyChange}
        dayCellDidMount={onDateCellMount}
        eventDidMount={onEventMount}
        eventWillUnmount={onEventUnmount}
      />
      </div>

      {agendaMode && (
        <AgendaView
          visibleCalendarIds={visibleCalendarIds}
          visibleCategoryIds={effectiveCategoryIds}
          calPickerLabel={calPickerLabel}
          catPickerLabel={catPickerLabel}
          onOpenCalPicker={openCalPop}
          onOpenCatPicker={openCatPop}
          onNew={openNew}
          onExit={exitAgenda}
          onEdit={openForEdit}
          onEventContext={(x, y, e) =>
            setMenu({ kind: 'event', x, y, seriesId: e.seriesId, recurring: e.recurring, occurrenceStart: e.occurrenceStart })
          }
        />
      )}

      {menu && (
        <div
          className="ctx-backdrop"
          onMouseDown={() => setMenu(null)}
          onContextMenu={(e) => {
            e.preventDefault()
            setMenu(null)
          }}
        >
          <div
            className="ctx-menu"
            style={{ left: Math.min(menu.x, window.innerWidth - 200), top: Math.min(menu.y, window.innerHeight - 140) }}
            onMouseDown={(e) => e.stopPropagation()}
          >
            {menu.kind === 'date' ? (
              <>
                <button className="ctx-item" onClick={() => { openNewOn(menu.date, false); setMenu(null) }}>
                  New appointment
                </button>
                <button className="ctx-item" onClick={() => { openNewOn(menu.date, true); setMenu(null) }}>
                  New all-day event
                </button>
                {canPaste && (
                  <button className="ctx-item" onClick={() => { doPaste(dayKey(menu.date)); setMenu(null) }}>
                    Paste appointment
                  </button>
                )}
              </>
            ) : (
              <>
                <button className="ctx-item" onClick={() => { openForEdit(menu.seriesId); setMenu(null) }}>
                  {menu.recurring ? 'Edit series' : 'Edit'}
                </button>
                <button className="ctx-item" onClick={() => { copyEvent(menu.seriesId); setMenu(null) }}>
                  Copy
                </button>
                {menu.recurring ? (
                  <>
                    <button className="ctx-item" onClick={() => { remove(menu.seriesId, menu.occurrenceStart); setMenu(null) }}>
                      Delete this occurrence
                    </button>
                    <button className="ctx-item danger" onClick={() => { remove(menu.seriesId); setMenu(null) }}>
                      Delete series
                    </button>
                  </>
                ) : (
                  <button className="ctx-item danger" onClick={() => { remove(menu.seriesId); setMenu(null) }}>
                    Delete
                  </button>
                )}
              </>
            )}
          </div>
        </div>
      )}

      {calPop && (
        <Popover anchor={calPop} onClose={() => setCalPop(null)} className="cal-switcher-menu">
            {calendars.length > 1 && (
              <>
                <button type="button" className="cal-switcher-item" onClick={toggleAllCals}>
                  <span className={'cal-check' + (shownCount === calendars.length ? ' on' : '')} />
                  All calendars
                </button>
                <div className="cal-switcher-divider" />
                {calendars.map((c) => (
                  <div key={c.id} className="cal-switcher-row">
                    <button type="button" className="cal-switcher-item" onClick={() => toggleCal(c.id)}>
                      <span className={'cal-check' + (isCalVisible(c.id) ? ' on' : '')} />
                      <span className="cal-switcher-name">{c.name}</span>
                      <span className="cal-switcher-count">{c.eventCount}</span>
                    </button>
                    <button
                      type="button"
                      className="cal-switcher-only"
                      onClick={() => onChangeVisible?.(calendars.length === 1 ? null : [c.id])}
                    >
                      only
                    </button>
                  </div>
                ))}
                <div className="cal-switcher-divider" />
              </>
            )}
            <button
              type="button"
              className="cal-switcher-item cal-switcher-manage"
              onClick={() => {
                setCalPop(null)
                onManage?.()
              }}
            >
              Manage calendars…
            </button>
        </Popover>
      )}

      {catPop && (
        <Popover anchor={catPop} onClose={() => setCatPop(null)} className="cal-switcher-menu">
            {categories.length > 0 && (
              <>
                <button type="button" className="cal-switcher-item" onClick={toggleAllCats}>
                  <span className={'cal-check' + (shownCatCount === allCatIds.length ? ' on' : '')} />
                  All categories
                </button>
                <div className="cal-switcher-divider" />
                {categories.map((c) => (
                  <div key={c.id} className="cal-switcher-row">
                    <button type="button" className="cal-switcher-item" onClick={() => toggleCat(c.id)}>
                      <span className={'cal-check' + (isCatVisible(c.id) ? ' on' : '')} />
                      <span className="cat-dot" style={{ background: c.color }} aria-hidden="true" />
                      <span className="cal-switcher-name">{c.name}</span>
                      {/* Upcoming appointments, not the all-time total — matches what the list view can actually show. */}
                      <span className="cal-switcher-count">{c.upcomingEventCount}</span>
                    </button>
                    <button type="button" className="cal-switcher-only" onClick={() => onChangeVisibleCategories?.([c.id])}>
                      only
                    </button>
                  </div>
                ))}
                <div className="cal-switcher-row">
                  <button type="button" className="cal-switcher-item" onClick={() => toggleCat(UNCATEGORIZED)}>
                    <span className={'cal-check' + (isCatVisible(UNCATEGORIZED) ? ' on' : '')} />
                    <span className="cat-dot cat-dot-none" aria-hidden="true" />
                    <span className="cal-switcher-name">Uncategorized</span>
                  </button>
                  <button type="button" className="cal-switcher-only" onClick={() => onChangeVisibleCategories?.([UNCATEGORIZED])}>
                    only
                  </button>
                </div>
                <div className="cal-switcher-divider" />
              </>
            )}
            <button
              type="button"
              className="cal-switcher-item cal-switcher-manage"
              onClick={() => {
                setCatPop(null)
                onManageCategories?.()
              }}
            >
              Manage categories…
            </button>
        </Popover>
      )}

      {yearPop && (
        <Popover anchor={yearPop} onClose={() => setYearPop(null)} className="year-pop">
            <div className="year-pop-nav">
              <button type="button" aria-label="Earlier years" onClick={() => setYearPop({ ...yearPop, base: yearPop.base - 12 })}>
                ‹
              </button>
              <span>
                {yearPop.base} – {yearPop.base + 11}
              </span>
              <button type="button" aria-label="Later years" onClick={() => setYearPop({ ...yearPop, base: yearPop.base + 12 })}>
                ›
              </button>
            </div>
            <div className="year-pop-grid">
              {Array.from({ length: 12 }, (_, i) => yearPop.base + i).map((y) => (
                <button
                  key={y}
                  type="button"
                  className={y === yearPop.current ? 'active' : ''}
                  onClick={() => pickYear(y)}
                >
                  {y}
                </button>
              ))}
            </div>
        </Popover>
      )}

      {draft && (
        <EventModal
          draft={draft}
          calendars={calendars}
          onSave={save}
          onDelete={draft.id ? (id) => remove(id) : undefined}
          onCopy={draft.id ? (id) => copyEvent(id) : undefined}
          onRespond={draft.id && draft.invitationStatus ? (status) => respond(draft.id!, status) : undefined}
          onClose={closeDraft}
        />
      )}

      {toast && (
        <div className="undo-toast" role="status" aria-live="polite">
          {toast}
        </div>
      )}
    </>
  )
}
