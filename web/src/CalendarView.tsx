import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import FullCalendar from '@fullcalendar/react'
import dayGridPlugin from '@fullcalendar/daygrid'
import timeGridPlugin from '@fullcalendar/timegrid'
import interactionPlugin from '@fullcalendar/interaction'
import type { DateClickArg } from '@fullcalendar/interaction'
import type {
  DatesSetArg,
  DayCellMountArg,
  EventMountArg,
  EventClickArg,
  EventChangeArg,
  EventInput,
} from '@fullcalendar/core'
import EventModal, { type EventDraft } from './EventModal'
import { createEvent, deleteEvent, getEvent, listEvents, updateEvent, type EventDto, type SaveEventRequest } from './api/events'
import { saveDefaultView } from './api/profile'
import { getSavedView, saveView } from './prefs'

const DEFAULT_COLOR = '#7B68EE' // mediumslateblue

const pad2 = (n: number) => String(n).padStart(2, '0')

function toLocalInput(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}T${pad2(date.getHours())}:${pad2(date.getMinutes())}`
}

function dayKey(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`
}

function hexToRgba(hex: string, alpha: number): string {
  const h = hex.replace('#', '')
  const full = h.length === 3 ? h.split('').map((c) => c + c).join('') : h
  const r = parseInt(full.slice(0, 2), 16)
  const g = parseInt(full.slice(2, 4), 16)
  const b = parseInt(full.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone

// API DTO → FullCalendar input. Recurring occurrences get a unique render id but carry
// the master id (seriesId) for edit/delete, and are not drag-editable in this phase.
function dtoToInput(dto: EventDto): EventInput {
  const color = dto.color ?? DEFAULT_COLOR
  return {
    id: dto.recurring ? `${dto.id}__${dto.start}` : dto.id,
    title: dto.title,
    start: dto.allDay ? dto.start.slice(0, 10) : dto.start,
    end: dto.end ? (dto.allDay ? dto.end.slice(0, 10) : dto.end) : undefined,
    allDay: dto.allDay,
    editable: !dto.recurring,
    backgroundColor: hexToRgba(color, 0.18),
    borderColor: color,
    extendedProps: {
      seriesId: dto.id,
      recurring: dto.recurring,
      occurrenceStart: dto.start,
      color,
      location: dto.location ?? '',
      description: dto.description ?? '',
      reminders: dto.reminders,
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
    title: d.title,
    description: d.description || null,
    location: d.location || null,
    color: d.color,
    start: toApiIso(d.start, d.allDay),
    end: d.end ? toApiIso(d.end, d.allDay) : null,
    allDay: d.allDay,
    recurrence: d.recurrence || null,
    timeZone: browserTz,
    reminders: d.reminders.map((r) => ({ minutesBefore: r.minutesBefore, channel: r.channel })),
  }
}

type ContextMenu =
  | { kind: 'date'; x: number; y: number; date: Date }
  | { kind: 'event'; x: number; y: number; seriesId: string; recurring: boolean; occurrenceStart: string }

const DOUBLE_CLICK_MS = 350

export default function CalendarView({
  focus,
  serverView,
}: {
  focus?: { date: string; n: number } | null
  serverView?: string | null
}) {
  const queryClient = useQueryClient()
  const [range, setRange] = useState<{ from: string; to: string } | null>(null)
  const { data: dtos = [] } = useQuery({
    queryKey: ['events', range?.from, range?.to],
    queryFn: () => listEvents(range!.from, range!.to),
    enabled: !!range,
  })
  const events = useMemo(() => dtos.map(dtoToInput), [dtos])

  const [draft, setDraft] = useState<EventDraft | null>(null)
  const [menu, setMenu] = useState<ContextMenu | null>(null)
  const [selectedDate, setSelectedDate] = useState<string | null>(null)
  const lastClick = useRef<{ dateStr: string; time: number } | null>(null)
  const calendarRef = useRef<FullCalendar>(null)
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
    if (!viewChanged || !selectedDate) return

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
    const api = calendarRef.current?.getApi()
    if (!api || api.view.type === serverView) return
    suppressPersist.current = true // don't PUT back a value we just read from the server
    api.changeView(serverView)
    saveView(serverView)
  }, [serverView])

  // A search pick (from the header) jumps the calendar to that appointment's day view.
  useEffect(() => {
    if (!focus) return
    const api = calendarRef.current?.getApi()
    if (!api) return
    const d = new Date(focus.date)
    api.changeView('timeGridDay', d)
    setSelectedDate(dayKey(d))
  }, [focus])

  // Left/Right arrow keys page the calendar (prev/next), matching the toolbar buttons.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
      if (draft || menu) return // don't steal keys from the editor / context menu
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
  }, [draft, menu])

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

  const openNew = () => {
    const start = new Date()
    start.setMinutes(0, 0, 0)
    start.setHours(start.getHours() + 1)
    openNewOn(start, false)
  }

  const openNewOn = (date: Date, allDay: boolean) => {
    const blank = { title: '', color: DEFAULT_COLOR, location: '', description: '', recurrence: '', reminders: [] }
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

  const handleDateClick = (arg: DateClickArg) => {
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
    setDraft({
      id: dto.id,
      title: dto.title,
      allDay,
      start: startLocal,
      end: endLocal,
      color: dto.color ?? DEFAULT_COLOR,
      location: dto.location ?? '',
      description: dto.description ?? '',
      recurrence: dto.recurrence ?? '',
      reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
    })
  }

  const save = (d: EventDraft) => {
    const body = draftToRequest(d)
    if (d.id) updateMut.mutate({ id: d.id, body })
    else createMut.mutate(body)
    setDraft(null)
  }

  const remove = (id: string, occurrence?: string) => {
    deleteMut.mutate({ id, occurrence })
    setDraft(null)
  }

  // Only fires for single events (recurring occurrences are not drag-editable).
  const applyChange = (info: EventChangeArg) => {
    const ev = info.event
    const body: SaveEventRequest = {
      title: ev.title,
      description: (ev.extendedProps.description as string) || null,
      location: (ev.extendedProps.location as string) || null,
      color: (ev.extendedProps.color as string) ?? DEFAULT_COLOR,
      start: ev.allDay ? toApiIso(ev.startStr, true) : (ev.start ?? new Date()).toISOString(),
      end: ev.end ? (ev.allDay ? toApiIso(ev.endStr, true) : ev.end.toISOString()) : null,
      allDay: ev.allDay,
      recurrence: null,
      timeZone: browserTz,
      reminders: (ev.extendedProps.reminders as { minutesBefore: number; channel: string }[] | undefined)?.map((r) => ({
        minutesBefore: r.minutesBefore,
        channel: r.channel,
      })) ?? null,
    }
    updateMut.mutate({ id: ev.extendedProps.seriesId as string, body })
  }

  const onDateCellMount = (arg: DayCellMountArg) => {
    arg.el.addEventListener('contextmenu', (e) => {
      e.preventDefault()
      setMenu({ kind: 'date', x: e.clientX, y: e.clientY, date: arg.date })
    })
  }

  const onEventMount = (arg: EventMountArg) => {
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

  return (
    <>
      <FullCalendar
        ref={calendarRef}
        plugins={[dayGridPlugin, timeGridPlugin, interactionPlugin]}
        initialView={getSavedView()}
        customButtons={{ addEvent: { text: '+  New', click: openNew } }}
        headerToolbar={{
          left: 'addEvent',
          center: 'title',
          right: 'prev,next today dayGridMonth,timeGridWeek,timeGridDay',
        }}
        height="100%"
        nowIndicator
        slotEventOverlap={false}
        editable
        dayMaxEvents={4}
        events={events}
        datesSet={handleDatesSet}
        dateClick={handleDateClick}
        dayCellClassNames={(arg) => (selectedDate && dayKey(arg.date) === selectedDate ? ['is-selected'] : [])}
        eventClick={(info: EventClickArg) => openForEdit(info.event.extendedProps.seriesId)}
        eventChange={applyChange}
        dayCellDidMount={onDateCellMount}
        eventDidMount={onEventMount}
      />

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
              </>
            ) : (
              <>
                <button className="ctx-item" onClick={() => { openForEdit(menu.seriesId); setMenu(null) }}>
                  {menu.recurring ? 'Edit series' : 'Edit'}
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

      {draft && (
        <EventModal
          draft={draft}
          onSave={save}
          onDelete={draft.id ? (id) => remove(id) : undefined}
          onClose={() => setDraft(null)}
        />
      )}
    </>
  )
}
