import { useEffect, useRef, useState } from 'react'
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

const DEFAULT_COLOR = '#7B68EE' // mediumslateblue (CSS3 name → clean CalDAV COLOR sync)

// Translucent fill + solid border from a base hex; base color kept in extendedProps
// so it round-trips into the edit modal (and, later, the API + iCalendar COLOR property).
function hexToRgba(hex: string, alpha: number): string {
  const h = hex.replace('#', '')
  const full = h.length === 3 ? h.split('').map((c) => c + c).join('') : h
  const r = parseInt(full.slice(0, 2), 16)
  const g = parseInt(full.slice(2, 4), 16)
  const b = parseInt(full.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

function colorProps(hex: string): EventInput {
  return { backgroundColor: hexToRgba(hex, 0.18), borderColor: hex, extendedProps: { color: hex } }
}

// Dated relative to today so the demo always looks populated. Replaced by API data in Phase 2.
function iso(offsetDays: number, time?: string) {
  const d = new Date()
  d.setDate(d.getDate() + offsetDays)
  const day = d.toISOString().slice(0, 10)
  return time ? `${day}T${time}` : day
}

const initialEvents: EventInput[] = [
  { id: 'seed-1', title: 'Sync review', start: iso(0, '10:00'), end: iso(0, '11:00'), ...colorProps('#7B68EE') },
  { id: 'seed-2', title: 'Deploy window', start: iso(1, '14:30'), end: iso(1, '16:00'), ...colorProps('#40E0D0') },
  { id: 'seed-3', title: 'Design critique', start: iso(3, '09:30'), end: iso(3, '10:30'), ...colorProps('#3CB371') },
  { id: 'seed-4', title: 'Release', allDay: true, start: iso(5), ...colorProps('#DAA520') },
]

const pad2 = (n: number) => String(n).padStart(2, '0')

// 'YYYY-MM-DDTHH:mm' in local time, for datetime-local inputs.
function toLocalInput(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}T${pad2(date.getHours())}:${pad2(date.getMinutes())}`
}

// Local day key 'YYYY-MM-DD', for matching the highlighted cell.
function dayKey(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`
}

type ContextMenu =
  | { kind: 'date'; x: number; y: number; date: Date }
  | { kind: 'event'; x: number; y: number; eventId: string }

const DOUBLE_CLICK_MS = 350

export default function CalendarView() {
  const [events, setEvents] = useState<EventInput[]>(initialEvents)
  const [draft, setDraft] = useState<EventDraft | null>(null)
  const [menu, setMenu] = useState<ContextMenu | null>(null)
  const [selectedDate, setSelectedDate] = useState<string | null>(null)
  const lastClick = useRef<{ dateStr: string; time: number } | null>(null)

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
    if (start.getHours() === 0 && start.getMinutes() === 0) start.setHours(9) // sensible default for day cells
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
      // Double-click always drafts a timed appointment; all-day is offered via right-click.
      openNewOn(arg.date, false)
    } else {
      lastClick.current = { dateStr: arg.dateStr, time: now }
    }
  }

  const openFromEvent = (info: EventClickArg) => openEventById(info.event.id)

  const openEventById = (id: string) => {
    const e = events.find((x) => x.id === id)
    if (!e) return
    const allDay = Boolean(e.allDay)
    const startStr = String(e.start)
    const endStr = e.end ? String(e.end) : startStr
    const color = (e.extendedProps?.color as string) ?? (e.borderColor as string) ?? DEFAULT_COLOR
    setDraft({
      id,
      title: (e.title as string) ?? '',
      allDay,
      start: allDay ? startStr.slice(0, 10) : startStr,
      end: allDay ? endStr.slice(0, 10) : endStr,
      color,
      location: (e.extendedProps?.location as string) ?? '',
      description: (e.extendedProps?.description as string) ?? '',
    })
  }

  const save = (d: EventDraft) => {
    setEvents((prev) => {
      const next: EventInput = {
        id: d.id ?? crypto.randomUUID(),
        title: d.title,
        start: d.start,
        end: d.end || undefined,
        allDay: d.allDay,
        ...colorProps(d.color),
        extendedProps: {
          color: d.color,
          location: d.location || undefined,
          description: d.description || undefined,
        },
      }
      return d.id ? prev.map((e) => (e.id === d.id ? { ...e, ...next } : e)) : [...prev, next]
    })
    setDraft(null)
  }

  const remove = (id: string) => {
    setEvents((prev) => prev.filter((e) => e.id !== id))
    setDraft(null)
  }

  // Persist drag/resize back into state so it survives re-render.
  const applyChange = (info: EventChangeArg) => {
    const ev = info.event
    setEvents((prev) =>
      prev.map((e) =>
        e.id === ev.id
          ? {
              ...e,
              start: ev.allDay ? ev.startStr : toLocalInput(ev.start ?? new Date()),
              end: ev.end ? (ev.allDay ? ev.endStr : toLocalInput(ev.end)) : undefined,
              allDay: ev.allDay,
            }
          : e,
      ),
    )
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
        eventClick={openFromEvent}
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
