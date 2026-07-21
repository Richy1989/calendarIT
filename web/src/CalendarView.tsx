import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import FullCalendar from '@fullcalendar/react'
import dayGridPlugin from '@fullcalendar/daygrid'
import timeGridPlugin from '@fullcalendar/timegrid'
import interactionPlugin from '@fullcalendar/interaction'
import type { DateClickArg } from '@fullcalendar/interaction'
import type {
  DayCellMountArg,
  EventMountArg,
  EventClickArg,
  EventChangeArg,
  EventInput,
} from '@fullcalendar/core'
import EventModal, { type EventDraft } from './EventModal'
import { createEvent, deleteEvent, listEvents, updateEvent, type EventDto, type SaveEventRequest } from './api/events'

const DEFAULT_COLOR = '#7B68EE' // mediumslateblue

const pad2 = (n: number) => String(n).padStart(2, '0')

// 'YYYY-MM-DDTHH:mm' in local time, for datetime-local inputs.
function toLocalInput(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}T${pad2(date.getHours())}:${pad2(date.getMinutes())}`
}

// Local day key 'YYYY-MM-DD', for matching the highlighted cell.
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

// API DTO (UTC times) → FullCalendar input. Base color lives in extendedProps so it
// round-trips into the edit modal.
function dtoToInput(dto: EventDto): EventInput {
  const color = dto.color ?? DEFAULT_COLOR
  return {
    id: dto.id,
    title: dto.title,
    start: dto.allDay ? dto.start.slice(0, 10) : dto.start,
    end: dto.end ? (dto.allDay ? dto.end.slice(0, 10) : dto.end) : undefined,
    allDay: dto.allDay,
    backgroundColor: hexToRgba(color, 0.18),
    borderColor: color,
    extendedProps: { color, location: dto.location ?? '', description: dto.description ?? '' },
  }
}

// Local input string → UTC ISO 8601 for the API.
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
  }
}

type ContextMenu =
  | { kind: 'date'; x: number; y: number; date: Date }
  | { kind: 'event'; x: number; y: number; eventId: string }

const DOUBLE_CLICK_MS = 350

export default function CalendarView() {
  const queryClient = useQueryClient()
  const { data: dtos = [] } = useQuery({ queryKey: ['events'], queryFn: listEvents })
  const events = useMemo(() => dtos.map(dtoToInput), [dtos])

  const [draft, setDraft] = useState<EventDraft | null>(null)
  const [menu, setMenu] = useState<ContextMenu | null>(null)
  const [selectedDate, setSelectedDate] = useState<string | null>(null)
  const lastClick = useRef<{ dateStr: string; time: number } | null>(null)

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['events'] })
  const createMut = useMutation({ mutationFn: createEvent, onSuccess: invalidate })
  const updateMut = useMutation({
    mutationFn: (v: { id: string; body: SaveEventRequest }) => updateEvent(v.id, v.body),
    onSuccess: invalidate,
  })
  const deleteMut = useMutation({ mutationFn: deleteEvent, onSuccess: invalidate })

  // Dismiss the context menu on scroll / resize / Escape.
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
    const blank = { title: '', color: DEFAULT_COLOR, location: '', description: '' }
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

  // Single click highlights the cell; a second click within the window creates.
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

  const openEventById = (id: string) => {
    const dto = dtos.find((d) => d.id === id)
    if (!dto) return
    const allDay = dto.allDay
    const startLocal = allDay ? dto.start.slice(0, 10) : toLocalInput(new Date(dto.start))
    const endLocal = dto.end
      ? allDay
        ? dto.end.slice(0, 10)
        : toLocalInput(new Date(dto.end))
      : startLocal
    setDraft({
      id: dto.id,
      title: dto.title,
      allDay,
      start: startLocal,
      end: endLocal,
      color: dto.color ?? DEFAULT_COLOR,
      location: dto.location ?? '',
      description: dto.description ?? '',
    })
  }

  const save = (d: EventDraft) => {
    const body = draftToRequest(d)
    if (d.id) updateMut.mutate({ id: d.id, body })
    else createMut.mutate(body)
    setDraft(null)
  }

  const remove = (id: string) => {
    deleteMut.mutate(id)
    setDraft(null)
  }

  // Persist a drag/resize.
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
    }
    updateMut.mutate({ id: ev.id, body })
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
      setMenu({ kind: 'event', x: e.clientX, y: e.clientY, eventId: arg.event.id })
    })
  }

  return (
    <>
      <FullCalendar
        plugins={[dayGridPlugin, timeGridPlugin, interactionPlugin]}
        initialView="dayGridMonth"
        customButtons={{ addEvent: { text: '+  New', click: openNew } }}
        headerToolbar={{
          left: 'addEvent',
          center: 'title',
          right: 'prev,next today dayGridMonth,timeGridWeek,timeGridDay',
        }}
        height="100%"
        nowIndicator
        editable
        dayMaxEvents={4}
        events={events}
        dateClick={handleDateClick}
        dayCellClassNames={(arg) => (selectedDate && dayKey(arg.date) === selectedDate ? ['is-selected'] : [])}
        eventClick={(info: EventClickArg) => openEventById(info.event.id)}
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
            style={{ left: Math.min(menu.x, window.innerWidth - 200), top: Math.min(menu.y, window.innerHeight - 110) }}
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
                <button className="ctx-item" onClick={() => { openEventById(menu.eventId); setMenu(null) }}>
                  Edit
                </button>
                <button className="ctx-item danger" onClick={() => { remove(menu.eventId); setMenu(null) }}>
                  Delete
                </button>
              </>
            )}
          </div>
        </div>
      )}

      {draft && (
        <EventModal
          draft={draft}
          onSave={save}
          onDelete={draft.id ? remove : undefined}
          onClose={() => setDraft(null)}
        />
      )}
    </>
  )
}
