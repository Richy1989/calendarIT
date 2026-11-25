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
import EventModal, { type EditScope, type EventDraft } from './EventModal'
import QuickCreate from './QuickCreate'
import { defaultRemindersFor } from './lib/eventForm'
import AgendaView from './AgendaView'
import ConfirmDialog from './components/ConfirmDialog'
import {
  createEvent,
  deleteEvent,
  deleteFollowing,
  exportEventIcs,
  getEvent,
  listEvents,
  resetOccurrence,
  respondToInvitation,
  updateEvent,
  updateFollowing,
  updateOccurrence,
  type EventDto,
  type RsvpStatus,
  type SaveEventRequest,
} from './api/events'
import { listCalendars } from './api/calendars'
import { listCategories } from './api/categories'
import { saveDefaultView } from './api/profile'
import { useHour12 } from './clock'
import { useFirstDay } from './weekStart'
import { Popover } from './components/Popover'
import { addDays, allDayDate, dayKey, formatTime, parseLocalValue, toLocalInput, toOffsetIso } from './lib/dates'
import { occurrenceOf, type OccurrenceRef } from './lib/occurrences'
import { useUndoStack } from './lib/useUndoStack'
import { NARROW_QUERY, useMediaQuery } from './lib/useMediaQuery'
import TaskPanel from './components/TaskPanel'
import Sidebar from './components/Sidebar'
import { FormIcon } from './components/FormIcons'
import TaskEditor from './components/TaskEditor'
import { createTask, deleteTask, listTasks, setTaskDone, taskToRequest, updateTask, type SaveTaskRequest, type TaskDto } from './api/tasks'
import { dueToApi, isDone } from './lib/tasks'
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

/** The id of the quick create form's preview event in the grid. */
const DRAFT_GHOST_ID = '__draft__'

function hexToRgba(hex: string, alpha: number): string {
  const h = hex.replace('#', '')
  const full = h.length === 3 ? h.split('').map((c) => c + c).join('') : h
  const r = parseInt(full.slice(0, 2), 16)
  const g = parseInt(full.slice(2, 4), 16)
  const b = parseInt(full.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone


// API DTO → FullCalendar input. Occurrences of a series get a render id unique per occurrence
// (series + its instant in the rule, stable even after the occurrence is moved) and carry what
// the occurrence endpoints need; dragging one moves just that occurrence.
const VIEW_LABELS: Record<string, string> = {
  dayGridMonth: 'Month',
  timeGridWeek: 'Week',
  timeGridDay: 'Day',
  agendaList: 'List',
}

function dtoToInput(dto: EventDto): EventInput {
  const color = dto.color ?? DEFAULT_COLOR
  const occurrence = occurrenceOf(dto)
  return {
    id: occurrence ? `${occurrence.seriesId}__${occurrence.occurrence}` : dto.id,
    title: dto.title,
    start: dto.allDay ? dto.start.slice(0, 10) : dto.start,
    // Stored all-day ends are inclusive; FullCalendar's are exclusive, so shift by a day
    // (otherwise multi-day all-day events render one day short).
    end: dto.end ? (dto.allDay ? addDays(dto.end.slice(0, 10), 1) : dto.end) : undefined,
    allDay: dto.allDay,
    backgroundColor: hexToRgba(color, 0.18),
    borderColor: color,
    extendedProps: {
      seriesId: occurrence?.seriesId ?? dto.id,
      calendarId: dto.calendarId,
      recurring: dto.recurring,
      occurrence,
      categoryId: dto.categoryId ?? null,
      location: dto.location ?? '',
      description: dto.description ?? '',
      reminders: dto.reminders,
      invitationStatus: dto.invitationStatus ?? null,
      organizerEmail: dto.organizerEmail ?? null,
      timeZone: dto.timeZone ?? null,
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
    // A new event lives in this browser's zone; an existing one keeps its own (null = keep).
    timeZone: d.timeZone === undefined ? browserTz : d.timeZone,
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
    timeZone: dto.timeZone ?? null,
    reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
    attendees: dto.attendees.map((a) => ({ email: a.email, name: a.name ?? null })),
  }
}

type ContextMenu =
  | { kind: 'date'; x: number; y: number; date: Date }
  | { kind: 'event'; x: number; y: number; seriesId: string; occurrence: OccurrenceRef | null; readOnly?: boolean }

/** The API instant (ISO) or day (all-day) → the editor's local field value. */
function toFieldValue(iso: string, allDay: boolean): string {
  return allDay ? iso.slice(0, 10) : toLocalInput(new Date(iso))
}

/** A server event → the editor's draft. */
function dtoToDraft(dto: EventDto): EventDraft {
  const startLocal = toFieldValue(dto.start, dto.allDay)
  return {
    id: dto.id,
    calendarId: dto.calendarId,
    title: dto.title,
    allDay: dto.allDay,
    start: startLocal,
    end: dto.end ? toFieldValue(dto.end, dto.allDay) : startLocal,
    categoryId: dto.categoryId ?? null,
    location: dto.location ?? '',
    description: dto.description ?? '',
    recurrence: dto.recurrence ?? '',
    reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
    attendees: dto.attendees.map((a) => ({ email: a.email, name: a.name, status: a.status })),
    invitationStatus: dto.invitationStatus ?? null,
    organizerEmail: dto.organizerEmail ?? null,
    timeZone: dto.timeZone ?? null,
  }
}

const DOUBLE_CLICK_MS = 350

export default function CalendarView({
  focus,
  toolbarStart,
  toolbarEnd,
  searchSlot,
  serverView,
  visibleCalendarIds,
  onChangeVisible,
  onManage,
  visibleCategoryIds,
  onChangeVisibleCategories,
  onManageCategories,
}: {
  focus?: { date: string; n: number } | null
  /** On a desktop: the app's own header items, put in the calendar's toolbar (logo at the start;
   *  search, clock and profile at the end) so the page has one bar instead of two. */
  toolbarStart?: React.ReactNode
  toolbarEnd?: React.ReactNode
  /** The search box: in the top bar it opens from an icon (typing anywhere still finds it). */
  searchSlot?: React.ReactNode
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
  const isNarrow = useMediaQuery(NARROW_QUERY)
  // On a desktop the app's own header joins the calendar's toolbar: one bar, more grid.
  const merged = !isNarrow && !!(toolbarStart || toolbarEnd || searchSlot)
  const [viewTitle, setViewTitle] = useState('')
  const [sidebarOpen, setSidebarOpen] = useState(() => localStorage.getItem('calendarit.sidebar') !== '0')
  const toggleSidebar = () =>
    setSidebarOpen((open) => {
      localStorage.setItem('calendarit.sidebar', open ? '0' : '1')
      return !open
    })
  const [viewPop, setViewPop] = useState<{ x: number; y: number } | null>(null)
  const [viewType, setViewType] = useState<string>('')
  const initialFcView = savedView === 'agendaList' ? 'dayGridMonth' : savedView
  const [range, setRange] = useState<{ from: string; to: string } | null>(null)
  const { data: dtos = [] } = useQuery({
    queryKey: ['events', range?.from, range?.to],
    queryFn: () => listEvents(range!.from, range!.to),
    enabled: !!range,
  })
  const { data: calendars = [] } = useQuery({ queryKey: ['calendars'], queryFn: listCalendars })
  const { data: openTasks = [] } = useQuery({ queryKey: ['tasks', false], queryFn: () => listTasks(false) })
  // Subscribed calendars are read-only copies of a feed: shown, never written to.
  const subscribedIds = useMemo(() => new Set(calendars.filter((c) => c.subscription).map((c) => c.id)), [calendars])
  const writableCalendars = useMemo(() => calendars.filter((c) => !c.subscription), [calendars])
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

  // What the grid shows — the sidebar's month marks the same appointments.
  const isEventShown = useCallback(
    (d: EventDto) =>
      d.invitationStatus !== 'Declined' &&
      (!visibleCalendarIds || visibleCalendarIds.includes(d.calendarId)) &&
      (!effectiveCategoryIds || effectiveCategoryIds.includes(d.effectiveCategoryId ?? d.categoryId ?? UNCATEGORIZED)),
    [visibleCalendarIds, effectiveCategoryIds],
  )

  const events = useMemo(
    () =>
      dtos
        .filter((d) => d.invitationStatus !== 'Declined') // a declined invitation drops off the calendar
        .filter((d) => !visibleCalendarIds || visibleCalendarIds.includes(d.calendarId))
        // By the category an event shows as: its own, else its calendar's default.
        .filter((d) => !effectiveCategoryIds || effectiveCategoryIds.includes(d.effectiveCategoryId ?? d.categoryId ?? UNCATEGORIZED))
        .map((d) => {
          const input = dtoToInput(d)
          return subscribedIds.has(d.calendarId)
            ? { ...input, editable: false, extendedProps: { ...input.extendedProps, readOnly: true } }
            : input
        }),
    [dtos, visibleCalendarIds, effectiveCategoryIds, subscribedIds],
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
    writableCalendars.find((c) => !visibleCalendarIds || visibleCalendarIds.includes(c.id))?.id ?? writableCalendars[0]?.id

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
  // A new event made in the grid starts in the quick create form; "More options" (or the list
  // view, which has no grid to anchor to) uses the full editor.
  const [quickMode, setQuickMode] = useState(false)
  const quickDraft = draft && quickMode && !draft.id ? draft : null

  // The color a draft shows in: its category's, else its calendar's default category's.
  const draftColor = (d: EventDraft) => {
    const own = d.categoryId ? categories.find((c) => c.id === d.categoryId) : undefined
    const inheritedId = calendars.find((c) => c.id === d.calendarId)?.defaultCategoryId
    const inherited = inheritedId ? categories.find((c) => c.id === inheritedId) : undefined
    return own?.color ?? inherited?.color ?? DEFAULT_COLOR
  }

  // Open tasks with a due date, on their day: a date-only one in the all-day row, a timed one
  // at its time. Dragging one moves its due date; its box ticks it off.
  const taskEvents = useMemo<EventInput[]>(
    () =>
      openTasks
        .filter((t) => t.due && (!visibleCalendarIds || visibleCalendarIds.includes(t.calendarId)))
        .map((t) => ({
          id: `task:${t.id}`,
          title: t.title,
          start: t.dueAllDay ? t.due!.slice(0, 10) : t.due!,
          end: t.dueAllDay ? undefined : new Date(new Date(t.due!).getTime() + 30 * 60_000).toISOString(),
          allDay: t.dueAllDay,
          durationEditable: false,
          classNames: ['fc-task', ...(t.priority === 'high' ? ['is-high'] : [])],
          extendedProps: { kind: 'task', taskId: t.id },
        })),
    [openTasks, visibleCalendarIds],
  )

  // While the quick form is open, the event it describes is drawn in the grid as it is typed.
  const gridEvents = useMemo(() => {
    if (!quickDraft) return [...events, ...taskEvents]
    const color = draftColor(quickDraft)
    const ghost: EventInput = {
      id: DRAFT_GHOST_ID,
      title: quickDraft.title.trim() || '(No title)',
      start: quickDraft.start,
      end: quickDraft.allDay ? addDays(quickDraft.end.slice(0, 10), 1) : quickDraft.end,
      allDay: quickDraft.allDay,
      backgroundColor: hexToRgba(color, 0.32),
      borderColor: color,
      classNames: ['fc-draft-ghost'],
      editable: false,
    }
    return [...events, ...taskEvents, ghost]
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [events, taskEvents, quickDraft, categories, calendars])

  // A quick draft moved to a day the grid doesn't show takes the grid there, so its preview
  // (and the form beside it) stays in view.
  const quickStart = quickDraft ? parseLocalValue(quickDraft.start, quickDraft.allDay).getTime() : null
  useEffect(() => {
    const api = calendarRef.current?.getApi()
    if (quickStart === null || !api) return
    const { activeStart, activeEnd } = api.view
    if (quickStart < activeStart.getTime() || quickStart >= activeEnd.getTime()) api.gotoDate(new Date(quickStart))
  }, [quickStart])

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

  // ---- Tasks: a panel beside the grid, and the ones with a due date on their day in it.
  const [tasksOpen, setTasksOpen] = useState(() => localStorage.getItem('calendarit.tasksOpen') === '1')
  const toggleTasks = () =>
    setTasksOpen((open) => {
      localStorage.setItem('calendarit.tasksOpen', open ? '0' : '1')
      return !open
    })
  /** The task editor: a task, or null for a new one; undefined when closed. */
  const [taskEdit, setTaskEdit] = useState<TaskDto | null | undefined>(undefined)
  const refreshTasks = () => queryClient.invalidateQueries({ queryKey: ['tasks'] })
  const taskSaveMut = useMutation({
    mutationFn: (v: { id?: string; body: SaveTaskRequest }) => (v.id ? updateTask(v.id, v.body) : createTask(v.body)),
    onSuccess: refreshTasks,
    onError: (e: Error) => showToast(e.message),
  })
  const taskDoneMut = useMutation({
    mutationFn: (t: TaskDto) => setTaskDone(t.id, !isDone(t)),
    onSuccess: refreshTasks,
    onError: (e: Error) => showToast(e.message),
  })
  const taskDeleteMut = useMutation({
    mutationFn: (id: string) => deleteTask(id),
    onSuccess: refreshTasks,
    onError: (e: Error) => showToast(e.message),
  })
  const findTask = (id: string) => openTasks.find((t) => t.id === id)
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
  const [selectedEvent, setSelectedEvent] = useState<{ id: string; seriesId: string; occurrence: OccurrenceRef | null } | null>(null)
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
    setRange({ from: toOffsetIso(arg.start), to: toOffsetIso(arg.end) })
    setViewTitle(arg.view.title)
    setViewType(arg.view.type)

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
    // A bare 'YYYY-MM-DD' (an all-day hit) is that day here, not midnight UTC.
    const d = focus.date.length === 10 ? allDayDate(focus.date) : new Date(focus.date)
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
  // A failed save used to vanish without a word; the server's reason (an unparseable repeat
  // rule, an end before the start) now reaches the user.
  const failed = (e: Error) => showToast(e.message || 'That didn’t work')
  const createMut = useMutation({ mutationFn: createEvent, onSuccess: invalidate, onError: failed })
  const updateMut = useMutation({
    mutationFn: (v: { id: string; body: SaveEventRequest }) => updateEvent(v.id, v.body),
    onSuccess: invalidate,
    onError: failed,
  })
  const deleteMut = useMutation({
    mutationFn: (v: { id: string; occurrence?: string }) => deleteEvent(v.id, v.occurrence),
    onSuccess: invalidate,
    onError: failed,
  })
  const occurrenceMut = useMutation({
    mutationFn: (v: { seriesId: string; occurrence: string; body: SaveEventRequest }) =>
      updateOccurrence(v.seriesId, v.occurrence, { ...v.body, recurrence: null }),
    onSuccess: invalidate,
    onError: failed,
  })
  const followingMut = useMutation({
    mutationFn: (v: { seriesId: string; occurrence: string; body: SaveEventRequest }) =>
      updateFollowing(v.seriesId, v.occurrence, v.body),
    onSuccess: invalidate,
    onError: failed,
  })
  const deleteFollowingMut = useMutation({
    mutationFn: (v: { seriesId: string; occurrence: string }) => deleteFollowing(v.seriesId, v.occurrence),
    onSuccess: invalidate,
    onError: failed,
  })
  const resetMut = useMutation({
    mutationFn: (v: { seriesId: string; occurrence: string }) => resetOccurrence(v.seriesId, v.occurrence),
    onSuccess: invalidate,
    onError: failed,
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

  // New events start in the first category (so they get a color without extra clicks) — unless
  // the calendar they land in has a category of its own, which they then simply inherit.
  const defaultCategoryId = () =>
    calendars.find((c) => c.id === defaultCalendarId())?.defaultCategoryId ? null : (categories[0]?.id ?? null)

  // A new event: in the first visible calendar, with its default category and — when timed —
  // its default reminders.
  const blankDraft = (allDay: boolean) => {
    const calendarId = defaultCalendarId()
    return {
      title: '', categoryId: defaultCategoryId(), location: '', description: '', recurrence: '', attendees: [], calendarId,
      reminders: defaultRemindersFor(calendars.find((c) => c.id === calendarId), allDay),
    }
  }

  // Opens a new event: the quick form beside its slot in the grid, the full editor in the list.
  const startNew = (d: EventDraft) => {
    setQuickMode(!agendaMode)
    setDraft(d)
    // The preview replaces the selection highlight.
    if (!agendaMode) calendarRef.current?.getApi().unselect()
  }

  const openNewOn = (date: Date, allDay: boolean) => {
    if (allDay) {
      const day = toLocalInput(date).slice(0, 10)
      startNew({ ...blankDraft(true), start: day, end: day, allDay: true })
      return
    }
    const start = new Date(date)
    if (start.getHours() === 0 && start.getMinutes() === 0) start.setHours(9)
    const end = new Date(start.getTime() + 60 * 60 * 1000)
    startNew({ ...blankDraft(false), start: toLocalInput(start), end: toLocalInput(end), allDay: false })
  }

  // Drag-selection (mouse, as in any calendar): day view picks a time range in hours,
  // week view a start/end time possibly spanning days, month view a span of days.
  // Opens the new-appointment editor prefilled with exactly what was selected.
  const handleSelect = (arg: DateSelectArg) => {
    clearEventSelection()
    setSelectedDate(dayKey(arg.start))
    if (arg.allDay) {
      const startDay = dayKey(arg.start)
      const endDay = addDays(dayKey(arg.end), -1) // exclusive → inclusive last day
      startNew({ ...blankDraft(true), start: startDay, end: endDay < startDay ? startDay : endDay, allDay: true })
    } else {
      startNew({ ...blankDraft(false), start: toLocalInput(arg.start), end: toLocalInput(arg.end), allDay: false })
    }
  }

  // Closes the editor and clears any pending drag-selection highlight behind it.
  const closeDraft = () => {
    setDraft(null)
    setQuickMode(false)
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
    setSelectedEvent({ id: ev.id, seriesId: ev.extendedProps.seriesId as string, occurrence: ev.extendedProps.occurrence ?? null })
  }

  const handleEventClick = (info: EventClickArg) => {
    if (info.event.id === DRAFT_GHOST_ID) return // the quick form's own preview
    if (info.event.extendedProps.kind === 'task') {
      const task = findTask(info.event.extendedProps.taskId as string)
      // Its box ticks it off; anywhere else opens it.
      if ((info.jsEvent.target as HTMLElement).closest('.fc-task-check')) {
        if (task) taskDoneMut.mutate(task)
      } else if (task) {
        setTaskEdit(task)
      }
      return
    }
    if (isCoarsePointer()) {
      // touch: a tap opens, as before
      void openForEdit(info.event.extendedProps.seriesId, info.event.extendedProps.occurrence ?? null)
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

  // The occurrence the open editor was opened on (null for a one-off) — kept so the editor can
  // switch between "this occurrence" and "all occurrences" without losing its place.
  const [draftOccurrence, setDraftOccurrence] = useState<OccurrenceRef | null>(null)

  /**
   * Opens the editor. A one-off event loads as itself. An occurrence of a series opens on just
   * that occurrence by default — its own edits if it has some, else the series' details at this
   * occurrence's times — or on the whole series (the master, unexpanded) with scope 'series'.
   */
  const openForEdit = async (id: string, occurrence: OccurrenceRef | null = null, scope: EditScope = 'occurrence') => {
    try {
      let loaded: EventDraft
      if (!occurrence) {
        loaded = dtoToDraft(await getEvent(id))
      } else if (scope === 'series') {
        loaded = { ...dtoToDraft(await getEvent(occurrence.seriesId)), seriesId: occurrence.seriesId, occurrence: occurrence.occurrence, scope }
      } else if (scope === 'following') {
        // The series as it stands, at this occurrence's slot in the rule (not where an edit of
        // it may have moved it): the new series starts there unless the times are changed.
        const series = await getEvent(occurrence.seriesId)
        const span = series.end ? new Date(series.end).getTime() - new Date(series.start).getTime() : 0
        const end = new Date(new Date(occurrence.occurrence).getTime() + span).toISOString()
        loaded = {
          ...dtoToDraft(series),
          seriesId: occurrence.seriesId,
          occurrence: occurrence.occurrence,
          scope,
          start: toFieldValue(occurrence.occurrence, series.allDay),
          end: toFieldValue(end, series.allDay),
        }
      } else if (occurrence.overrideId) {
        loaded = { ...dtoToDraft(await getEvent(occurrence.overrideId)), seriesId: occurrence.seriesId, occurrence: occurrence.occurrence, scope, isOverride: true }
      } else {
        const series = dtoToDraft(await getEvent(occurrence.seriesId))
        loaded = {
          ...series,
          seriesId: occurrence.seriesId,
          occurrence: occurrence.occurrence,
          scope,
          recurrence: '',
          start: toFieldValue(occurrence.start, occurrence.allDay),
          end: toFieldValue(occurrence.end ?? occurrence.start, occurrence.allDay),
        }
      }
      setDraftOccurrence(occurrence)
      originalEdit.current = loaded // snapshot: an edit's undo restores this state
      setDraft(loaded)
    } catch {
      showToast('Couldn’t open that appointment')
    }
  }

  const respond = (id: string, status: RsvpStatus) => {
    rsvpMut.mutate({ id, status })
    closeDraft()
  }

  const save = (d: EventDraft) => {
    const body = draftToRequest(d)
    const label = d.title.trim() || 'event'
    if (d.seriesId && d.scope === 'following' && d.occurrence) {
      // A split can't be taken back with Ctrl+Z: it gives the later part a new identity (for
      // phones and guests), and putting the halves together again isn't something to guess at.
      followingMut.mutate(
        { seriesId: d.seriesId, occurrence: d.occurrence, body },
        { onSuccess: () => showToast(`Changed “${label}” from this occurrence on`) },
      )
    } else if (d.seriesId && d.scope === 'occurrence' && d.occurrence) {
      const seriesId = d.seriesId
      const occurrence = d.occurrence
      const prev = originalEdit.current
      const prevBody = prev?.seriesId === seriesId && prev.occurrence === occurrence ? draftToRequest(prev) : null
      // Undo puts the occurrence back as it was: its earlier edits, or plain series again.
      const restore = () =>
        d.isOverride && prevBody
          ? occurrenceMut.mutateAsync({ seriesId, occurrence, body: prevBody })
          : resetMut.mutateAsync({ seriesId, occurrence })
      occurrenceMut.mutate(
        { seriesId, occurrence, body },
        {
          onSuccess: () =>
            record({
              label: `edited “${label}” (this occurrence)`,
              undo: restore,
              redo: () => occurrenceMut.mutateAsync({ seriesId, occurrence, body }),
            }),
        },
      )
    } else if (d.id) {
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

  // Deletes a whole event (a series with all its occurrences). Undo recreates it.
  const remove = async (id: string) => {
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

  // Deletes one occurrence of a series. Undo brings it back — with its own edits, if it had some.
  const removeOccurrence = async (ref: OccurrenceRef) => {
    const { seriesId, occurrence } = ref
    const edited = ref.overrideId ? await getEvent(ref.overrideId).catch(() => null) : null
    deleteMut.mutate(
      { id: seriesId, occurrence },
      {
        onSuccess: () =>
          record({
            label: 'deleted an occurrence',
            undo: async () => {
              await resetMut.mutateAsync({ seriesId, occurrence })
              if (edited) await occurrenceMut.mutateAsync({ seriesId, occurrence, body: dtoToRequest(edited) })
            },
            redo: () => deleteMut.mutateAsync({ id: seriesId, occurrence }),
          }),
      },
    )
    closeDraft()
  }

  // Deletes an occurrence and every later one. Like a split, not undoable — so it asks first.
  const [confirmFollowing, setConfirmFollowing] = useState<OccurrenceRef | null>(null)
  const removeFollowing = (ref: OccurrenceRef) => setConfirmFollowing(ref)
  const doRemoveFollowing = (ref: OccurrenceRef) => {
    setConfirmFollowing(null)
    deleteFollowingMut.mutate(
      { seriesId: ref.seriesId, occurrence: ref.occurrence },
      { onSuccess: () => showToast('Deleted this and the following occurrences') },
    )
    closeDraft()
  }

  // Drops an occurrence's own edits, so it follows the series again. Undo re-applies them.
  const resetToSeries = async (ref: OccurrenceRef) => {
    if (!ref.overrideId) return
    const edited = await getEvent(ref.overrideId).catch(() => null)
    const { seriesId, occurrence } = ref
    resetMut.mutate(
      { seriesId, occurrence },
      {
        onSuccess: () => {
          if (!edited) return
          const body = dtoToRequest(edited)
          record({
            label: `reset “${edited.title || 'occurrence'}” to the series`,
            undo: () => occurrenceMut.mutateAsync({ seriesId, occurrence, body }),
            redo: () => resetMut.mutateAsync({ seriesId, occurrence }),
          })
        },
      },
    )
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
    if (!draft.calendarId || !writableCalendars.some((c) => c.id === draft.calendarId)) draft.calendarId = defaultCalendarId()
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
  const removeSelRef = useRef((sel: { seriesId: string; occurrence: OccurrenceRef | null }) =>
    sel.occurrence ? removeOccurrence(sel.occurrence) : remove(sel.seriesId))
  removeSelRef.current = (sel) => (sel.occurrence ? removeOccurrence(sel.occurrence) : remove(sel.seriesId))
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (draft || menu || yearPop || calPop || catPop) return
      if (e.key === 'Escape') { clearEventSelection(); return }
      const target = e.target as HTMLElement | null
      const tag = target?.tagName
      if (target?.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return

      // Delete / Backspace removes the selected appointment — for a series, just the selected
      // occurrence (undoable).
      if (e.key === 'Delete' || e.key === 'Backspace') {
        if (!selectedEvent) return
        e.preventDefault()
        const sel = selectedEvent
        clearEventSelection()
        void removeSelRef.current(sel)
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

  // Fires after a drag or an edge-resize. Sends just the fields a grid edit can touch: the API
  // leaves attendees and the owning calendar alone when they're absent, so guests survive a
  // resize untouched. On an occurrence of a series it moves only that occurrence.
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
    timeZone: (ev.extendedProps.timeZone as string | null | undefined) ?? null, // the event's own, never the browser's
    reminders: (ev.extendedProps.reminders as { minutesBefore: number; channel: string }[] | undefined)?.map((r) => ({
      minutesBefore: r.minutesBefore,
      channel: r.channel,
    })) ?? null,
  })

  const applyChange = (info: EventChangeArg) => {
    if (info.event.extendedProps.kind === 'task') {
      const task = findTask(info.event.extendedProps.taskId as string)
      const start = info.event.start
      if (!task || !start) {
        info.revert()
        return
      }
      // Dropped into the all-day row it becomes a date; into a time slot, that time.
      const allDay = info.event.allDay
      const body = {
        ...taskToRequest(task),
        due: dueToApi(allDay ? dayKey(start) : toLocalInput(start), allDay),
        dueAllDay: allDay,
        timeZone: allDay ? null : (task.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone),
      }
      taskSaveMut.mutate({ id: task.id, body }, { onError: () => info.revert() })
      return
    }
    const id = info.event.extendedProps.seriesId as string
    const newBody = eventToBody(info.event)
    const oldBody = eventToBody(info.oldEvent) // position before the drag — the undo target
    const ref = info.event.extendedProps.occurrence as OccurrenceRef | null
    if (ref) {
      const { seriesId, occurrence } = ref
      occurrenceMut.mutate(
        { seriesId, occurrence, body: newBody },
        {
          onError: () => info.revert(),
          onSuccess: () =>
            record({
              label: `moved “${info.event.title || 'event'}” (this occurrence)`,
              // Back where it was: its earlier edits, or plain series again.
              undo: () =>
                ref.overrideId
                  ? occurrenceMut.mutateAsync({ seriesId, occurrence, body: oldBody })
                  : resetMut.mutateAsync({ seriesId, occurrence }),
              redo: () => occurrenceMut.mutateAsync({ seriesId, occurrence, body: newBody }),
            }),
        },
      )
      return
    }
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
    if (arg.event.extendedProps.kind === 'task') return // a task: clicks are handled in handleEventClick
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
      void openForEdit(arg.event.extendedProps.seriesId, arg.event.extendedProps.occurrence ?? null)
    })
    arg.el.addEventListener('contextmenu', (e) => {
      e.preventDefault()
      e.stopPropagation()
      setMenu({
        kind: 'event',
        x: e.clientX,
        y: e.clientY,
        seriesId: arg.event.extendedProps.seriesId,
        occurrence: arg.event.extendedProps.occurrence ?? null,
        readOnly: !!arg.event.extendedProps.readOnly,
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
      <div className={'cal-layout' + (merged ? ' is-merged' : '')}>
      {merged && sidebarOpen && (
        <Sidebar
          brand={toolbarStart}
          onCollapse={toggleSidebar}
          onNew={openNew}
          tasksOpen={tasksOpen}
          onToggleTasks={toggleTasks}
          selectedDate={new Date(`${selectedDate}T00:00:00`)}
          onPickDate={(day) => {
            if (agendaMode) exitAgenda('timeGridDay')
            calendarRef.current?.getApi().gotoDate(day)
            setSelectedDate(dayKey(day))
          }}
          calendars={calendars}
          categories={categories}
          calendarColor={(c) => categories.find((x) => x.id === c.defaultCategoryId)?.color ?? 'var(--accent)'}
          isCalVisible={isCalVisible}
          onToggleCal={toggleCal}
          onOnlyCal={(id) => onChangeVisible?.(calendars.length === 1 ? null : [id])}
          isCatVisible={isCatVisible}
          onToggleCat={toggleCat}
          onOnlyCat={(id) => onChangeVisibleCategories?.([id])}
          onManageCalendars={onManage}
          onManageCategories={onManageCategories}
          isShown={isEventShown}
        />
      )}
      <div className="cal-layout-main">
      {merged && (
        <div className="fc cal-topbar">
          <div className="fc-header-toolbar fc-toolbar">
            <div className="fc-toolbar-chunk cal-topbar-start">
              {!sidebarOpen && (
                <>
                  <button type="button" className="side-icon-btn" onClick={toggleSidebar} aria-label="Show sidebar" title="Show sidebar">
                    <svg viewBox="0 0 24 24" aria-hidden="true">
                      <rect x="3" y="4" width="18" height="16" rx="2.5" />
                      <path d="M9 4v16" />
                      <path d="M13.5 10l2 2-2 2" />
                    </svg>
                  </button>
                  {toolbarStart}
                  <button type="button" className="fc-button fc-button-primary fc-addEvent-button" onClick={openNew}>
                    +  New
                  </button>
                </>
              )}
              {!agendaMode && (
                <>
                  <button type="button" className="fc-todaySelect-button fc-button fc-button-primary" onClick={goToday}>
                    today
                  </button>
                  <div className="fc-button-group">
                    <button type="button" className="fc-prev-button fc-button fc-button-primary" aria-label="Previous" onClick={() => calendarRef.current?.getApi().prev()}>
                      <span className="fc-icon fc-icon-chevron-left" />
                    </button>
                    <button type="button" className="fc-next-button fc-button fc-button-primary" aria-label="Next" onClick={() => calendarRef.current?.getApi().next()}>
                      <span className="fc-icon fc-icon-chevron-right" />
                    </button>
                  </div>
                </>
              )}
              {agendaMode ? <h2 className="agenda-title">Upcoming</h2> : <h2 className="fc-toolbar-title">{viewTitle}</h2>}
            </div>
            <div className="fc-toolbar-chunk cal-topbar-end">
              <button
                type="button"
                className="fc-button fc-button-primary cal-view-btn"
                aria-haspopup="menu"
                aria-expanded={!!viewPop}
                onClick={(e) => {
                  const r = e.currentTarget.getBoundingClientRect()
                  setViewPop((p) => (p ? null : { x: r.right - 160, y: r.bottom + 6 }))
                }}
              >
                {VIEW_LABELS[agendaMode ? 'agendaList' : viewType] ?? 'View'}
                <FormIcon name="chevron" className="form-icon-sm" />
              </button>
              {searchSlot && (
                <div className="topbar-search">
                  <button
                    type="button"
                    className="side-icon-btn"
                    aria-label="Search appointments"
                    title="Search (or just start typing)"
                    onClick={(e) => e.currentTarget.parentElement?.querySelector<HTMLInputElement>('input')?.focus()}
                  >
                    <svg viewBox="0 0 24 24" aria-hidden="true">
                      <circle cx="11" cy="11" r="6.5" />
                      <path d="M16 16l4.5 4.5" />
                    </svg>
                  </button>
                  <div className="topbar-search-panel">{searchSlot}</div>
                </div>
              )}
              {/* With the sidebar open its Tasks button does this; collapsed, this icon stands in. */}
              {!sidebarOpen && (
              <button
                type="button"
                className={'side-icon-btn' + (tasksOpen ? ' is-on' : '')}
                aria-pressed={tasksOpen}
                aria-label="Tasks"
                title="Tasks"
                onClick={toggleTasks}
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <rect x="4" y="4" width="16" height="16" rx="3" />
                  <path d="M8.5 12l2.5 2.5 4.5-5" />
                </svg>
              </button>
              )}
              {toolbarEnd}
            </div>
          </div>
        </div>
      )}
      <div className="cal-main-row">
      <div className="cal-main-col">
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
          tasksBtn: { text: tasksOpen ? '✓ Tasks' : 'Tasks', click: toggleTasks },
        }}
        headerToolbar={
          merged
            ? false
            : isNarrow
            ? { left: 'addEvent', center: 'title', right: 'prev,next todaySelect' }
            : {
                left: 'addEvent calPicker catPicker tasksBtn',
                center: 'title',
                right: 'prev,next todaySelect dayGridMonth,timeGridWeek,timeGridDay,listBtn',
              }
        }
        // On phones the pickers + view switch move to a bottom bar — thumb-reachable and
        // it frees the top row for the date. Desktop keeps everything in the header.
        footerToolbar={
          isNarrow
            ? { left: 'calPicker catPicker tasksBtn', right: 'dayGridMonth,timeGridWeek,timeGridDay,listBtn' }
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
        events={gridEvents}
        datesSet={handleDatesSet}
        dateClick={handleDateClick}
        select={handleSelect}
        // Which day is selected only means something where there are other days to tell it
        // apart from. Day view is that one day, so the highlight would just ring the whole
        // grid — and in cyan, competing with the now-indicator.
        dayCellClassNames={(arg) =>
          arg.view.type !== 'timeGridDay' && dayKey(arg.date) === selectedDate ? ['is-selected'] : []
        }
        eventContent={(arg) =>
          arg.event.extendedProps.kind === 'task' ? (
            <span className="fc-task-body">
              <span className="fc-task-check" role="checkbox" aria-checked={false} aria-label={`Tick off “${arg.event.title}”`} />
              <span className="fc-task-title">
                {/* A task is due at a moment, not over a span: just that time. */}
                {!arg.event.allDay && arg.event.start && <span className="fc-task-time">{formatTime(arg.event.start, hour12)} </span>}
                {arg.event.title}
              </span>
            </span>
          ) : (
            true
          )
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
          hideToolbar={merged}
          visibleCalendarIds={visibleCalendarIds}
          visibleCategoryIds={effectiveCategoryIds}
          calPickerLabel={calPickerLabel}
          catPickerLabel={catPickerLabel}
          onOpenCalPicker={openCalPop}
          onOpenCatPicker={openCatPop}
          onNew={openNew}
          onExit={exitAgenda}
          onEdit={(dto) => void openForEdit(dto.id, occurrenceOf(dto))}
          onEventContext={(x, y, dto) => {
            const occurrence = occurrenceOf(dto)
            setMenu({ kind: 'event', x, y, seriesId: occurrence?.seriesId ?? dto.id, occurrence, readOnly: subscribedIds.has(dto.calendarId) })
          }}
        />
      )}

      </div>
      {tasksOpen && (
        <TaskPanel
          defaultCalendarId={defaultCalendarId()}
          visibleCalendarIds={visibleCalendarIds ?? null}
          onOpen={(t) => setTaskEdit(t)}
          onClose={toggleTasks}
        />
      )}
      </div>
      </div>
      </div>

      {taskEdit !== undefined && (
        <TaskEditor
          key={taskEdit?.id ?? 'new'}
          task={taskEdit}
          calendars={writableCalendars}
          defaultCalendarId={defaultCalendarId()}
          onSave={(body) => {
            taskSaveMut.mutate({ id: taskEdit?.id, body })
            setTaskEdit(undefined)
          }}
          onDelete={
            taskEdit
              ? () => {
                  taskDeleteMut.mutate(taskEdit.id)
                  setTaskEdit(undefined)
                }
              : undefined
          }
          onClose={() => setTaskEdit(undefined)}
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
              menu.readOnly ? (
                <>
                  <button className="ctx-item" onClick={() => { void openForEdit(menu.seriesId, menu.occurrence); setMenu(null) }}>
                    Open
                  </button>
                  <button className="ctx-item" onClick={() => { copyEvent(menu.seriesId); setMenu(null) }}>
                    Copy
                  </button>
                </>
              ) : (
              <>
                {menu.occurrence ? (
                  <>
                    <button className="ctx-item" onClick={() => { void openForEdit(menu.seriesId, menu.occurrence, 'occurrence'); setMenu(null) }}>
                      Edit this occurrence
                    </button>
                    <button className="ctx-item" onClick={() => { void openForEdit(menu.seriesId, menu.occurrence, 'series'); setMenu(null) }}>
                      Edit series
                    </button>
                  </>
                ) : (
                  <button className="ctx-item" onClick={() => { void openForEdit(menu.seriesId); setMenu(null) }}>
                    Edit
                  </button>
                )}
                <button className="ctx-item" onClick={() => { copyEvent(menu.seriesId); setMenu(null) }}>
                  Copy
                </button>
                {menu.occurrence ? (
                  <>
                    {menu.occurrence.overrideId && (
                      <button className="ctx-item" onClick={() => { void resetToSeries(menu.occurrence!); setMenu(null) }}>
                        Reset to series
                      </button>
                    )}
                    <button className="ctx-item" onClick={() => { void removeOccurrence(menu.occurrence!); setMenu(null) }}>
                      Delete this occurrence
                    </button>
                    <button className="ctx-item danger" onClick={() => { removeFollowing(menu.occurrence!); setMenu(null) }}>
                      Delete this and following
                    </button>
                    <button className="ctx-item danger" onClick={() => { void remove(menu.seriesId); setMenu(null) }}>
                      Delete series
                    </button>
                  </>
                ) : (
                  <button className="ctx-item danger" onClick={() => { void remove(menu.seriesId); setMenu(null) }}>
                    Delete
                  </button>
                )}
              </>
              )
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
        quickDraft ? (
          <QuickCreate
            draft={quickDraft}
            calendars={writableCalendars}
            color={draftColor(quickDraft)}
            inheritedCategory={(() => {
              const id = calendars.find((c) => c.id === quickDraft.calendarId)?.defaultCategoryId
              return id ? categories.find((c) => c.id === id) : undefined
            })()}
            mobile={isNarrow}
            onChange={setDraft}
            onSave={save}
            onMore={(d) => {
              setQuickMode(false)
              setDraft(d)
            }}
            onClose={closeDraft}
          />
        ) : (
        <EventModal
          // A scope switch loads a different draft; remount so the fields start from it.
          key={`${draft.id ?? 'new'}:${draft.scope ?? ''}`}
          draft={draft}
          calendars={subscribedIds.has(draft.calendarId ?? '') ? calendars : writableCalendars}
          readOnly={subscribedIds.has(draft.calendarId ?? '')}
          onSave={save}
          onDelete={
            !draft.id
              ? undefined
              : draft.scope === 'occurrence' && draftOccurrence
                ? () => void removeOccurrence(draftOccurrence)
                : draft.scope === 'following' && draftOccurrence
                  ? () => removeFollowing(draftOccurrence)
                  : () => void remove(draft.seriesId ?? draft.id!)
          }
          onCopy={draft.id ? () => copyEvent(draft.seriesId ?? draft.id!) : undefined}
          onRespond={draft.id && draft.invitationStatus ? (status) => respond(draft.seriesId ?? draft.id!, status) : undefined}
          onScopeChange={draftOccurrence ? (scope) => void openForEdit(draftOccurrence.seriesId, draftOccurrence, scope) : undefined}
          onClose={closeDraft}
        />
        )
      )}

      {viewPop && (
        <Popover anchor={viewPop} onClose={() => setViewPop(null)} className="cal-switcher-menu cal-view-menu">
          {(['dayGridMonth', 'timeGridWeek', 'timeGridDay', 'agendaList'] as const).map((v) => {
            const current = agendaMode ? v === 'agendaList' : v === viewType
            return (
              <button
                key={v}
                type="button"
                className={'cal-switcher-item' + (current ? ' is-current' : '')}
                onClick={() => {
                  setViewPop(null)
                  if (v === 'agendaList') {
                    if (!agendaMode) enterAgenda()
                  } else if (agendaMode) {
                    exitAgenda(v)
                  } else {
                    calendarRef.current?.getApi().changeView(v)
                  }
                }}
              >
                <span className="cal-switcher-name">{VIEW_LABELS[v]}</span>
              </button>
            )
          })}
        </Popover>
      )}

      {/* After the editor, so it opens over it. */}
      {confirmFollowing && (
        <ConfirmDialog
          title="Delete this and following"
          message="Delete this occurrence and every later one? Earlier occurrences stay. This can't be undone."
          onConfirm={() => doRemoveFollowing(confirmFollowing)}
          onClose={() => setConfirmFollowing(null)}
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
