import { useEffect, useMemo, useRef, useState } from 'react'
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
  EventMountArg,
  EventClickArg,
  EventChangeArg,
  EventInput,
} from '@fullcalendar/core'
import EventModal, { type EventDraft } from './EventModal'
import { createEvent, deleteEvent, getEvent, listEvents, updateEvent, type EventDto, type SaveEventRequest } from './api/events'
import { listCalendars } from './api/calendars'
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

// Shifts a 'YYYY-MM-DD' day string by n days. Used to translate between FullCalendar's
// exclusive all-day end and the draft/API convention of an inclusive last day.
function addDays(day: string, n: number): string {
  const d = new Date(`${day}T00:00:00`)
  d.setDate(d.getDate() + n)
  return dayKey(d)
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
    calendarId: d.calendarId ?? null,
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
    attendees: d.attendees.map((a) => ({ email: a.email, name: a.name ?? null })),
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
}: {
  focus?: { date: string; n: number } | null
  serverView?: string | null
  /** Calendars to show; null/undefined = all of them. */
  visibleCalendarIds?: string[] | null
  /** Called when the user toggles calendar visibility in the toolbar picker. */
  onChangeVisible?: (ids: string[] | null) => void
  /** Opens Settings → Calendars ("Manage calendars…"). */
  onManage?: () => void
}) {
  const queryClient = useQueryClient()
  const [range, setRange] = useState<{ from: string; to: string } | null>(null)
  const { data: dtos = [] } = useQuery({
    queryKey: ['events', range?.from, range?.to],
    queryFn: () => listEvents(range!.from, range!.to),
    enabled: !!range,
  })
  const { data: calendars = [] } = useQuery({ queryKey: ['calendars'], queryFn: listCalendars })
  const events = useMemo(
    () =>
      dtos
        .filter((d) => !visibleCalendarIds || visibleCalendarIds.includes(d.calendarId))
        .map(dtoToInput),
    [dtos, visibleCalendarIds],
  )

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

  const toggleCal = (id: string) => {
    const next = calendars.filter((c) => (c.id === id ? !isCalVisible(c.id) : isCalVisible(c.id))).map((c) => c.id)
    if (next.length === 0) return // never blank the whole view
    onChangeVisible?.(next.length === calendars.length ? null : next)
  }

  const [draft, setDraft] = useState<EventDraft | null>(null)
  const [menu, setMenu] = useState<ContextMenu | null>(null)
  // Year quick-jump popover, opened by clicking the toolbar title ("August 2026").
  // `base` is the first year of the visible 12-year window; `current` the year on screen.
  const [yearPop, setYearPop] = useState<{ x: number; y: number; base: number; current: number } | null>(null)
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
      if (draft || menu || yearPop || calPop) return // don't steal keys from the editor / popovers
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
  }, [draft, menu, yearPop, calPop])

  useEffect(() => {
    if (!calPop) return
    const close = () => setCalPop(null)
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setCalPop(null)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
      window.removeEventListener('keydown', onKey)
    }
  }, [calPop])

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

  useEffect(() => {
    if (!yearPop) return
    const close = () => setYearPop(null)
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setYearPop(null)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
      window.removeEventListener('keydown', onKey)
    }
  }, [yearPop])

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

  const openNewOn = (date: Date, allDay: boolean) => {
    const blank = { title: '', color: DEFAULT_COLOR, location: '', description: '', recurrence: '', reminders: [], attendees: [], calendarId: defaultCalendarId() }
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
    setSelectedDate(dayKey(arg.start))
    const blank = { title: '', color: DEFAULT_COLOR, location: '', description: '', recurrence: '', reminders: [], attendees: [], calendarId: defaultCalendarId() }
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
      calendarId: dto.calendarId,
      title: dto.title,
      allDay,
      start: startLocal,
      end: endLocal,
      color: dto.color ?? DEFAULT_COLOR,
      location: dto.location ?? '',
      description: dto.description ?? '',
      recurrence: dto.recurrence ?? '',
      reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
      attendees: dto.attendees.map((a) => ({ email: a.email, name: a.name, status: a.status })),
    })
  }

  const save = (d: EventDraft) => {
    const body = draftToRequest(d)
    if (d.id) updateMut.mutate({ id: d.id, body })
    else createMut.mutate(body)
    closeDraft()
  }

  const remove = (id: string, occurrence?: string) => {
    deleteMut.mutate({ id, occurrence })
    closeDraft()
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
      // FullCalendar's all-day end is exclusive; the API stores an inclusive last day.
      end: ev.end ? (ev.allDay ? toApiIso(addDays(ev.endStr.slice(0, 10), -1), true) : ev.end.toISOString()) : null,
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
        customButtons={{
          addEvent: { text: '+  New', click: openNew },
          todaySelect: { text: 'today', click: goToday },
          calPicker: {
            text: calPickerLabel,
            click: (_ev, element) => {
              const rect = element.getBoundingClientRect()
              setCalPop((p) => (p ? null : { x: rect.left, y: rect.bottom + 6 }))
            },
          },
        }}
        headerToolbar={{
          left: 'addEvent calPicker',
          center: 'title',
          right: 'prev,next todaySelect dayGridMonth,timeGridWeek,timeGridDay',
        }}
        height="100%"
        nowIndicator
        slotEventOverlap={false}
        editable
        selectable
        selectMirror
        selectMinDistance={8} // plain clicks keep their click/double-click behavior; only a real drag selects
        unselectAuto={false} // the highlight stays under the editor; closeDraft() clears it
        dayMaxEvents={4}
        events={events}
        datesSet={handleDatesSet}
        dateClick={handleDateClick}
        select={handleSelect}
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

      {calPop && (
        <div
          className="ctx-backdrop"
          onMouseDown={() => setCalPop(null)}
          onContextMenu={(e) => {
            e.preventDefault()
            setCalPop(null)
          }}
        >
          <div
            className="cal-switcher-menu"
            style={{ position: 'fixed', left: calPop.x, top: calPop.y }}
            onMouseDown={(e) => e.stopPropagation()}
          >
            {calendars.length > 1 && (
              <>
                <button type="button" className="cal-switcher-item" onClick={() => onChangeVisible?.(null)}>
                  <span className={'cal-check' + (shownCount === calendars.length ? ' on' : '')} />
                  All calendars
                </button>
                <div className="cal-switcher-divider" />
                {calendars.map((c) => (
                  <button key={c.id} type="button" className="cal-switcher-item" onClick={() => toggleCal(c.id)}>
                    <span className={'cal-check' + (isCalVisible(c.id) ? ' on' : '')} />
                    <span className="cal-switcher-name">{c.name}</span>
                    <span className="cal-switcher-count">{c.eventCount}</span>
                  </button>
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
          </div>
        </div>
      )}

      {yearPop && (
        <div
          className="ctx-backdrop"
          onMouseDown={() => setYearPop(null)}
          onContextMenu={(e) => {
            e.preventDefault()
            setYearPop(null)
          }}
        >
          <div
            className="year-pop"
            style={{ left: yearPop.x, top: yearPop.y }}
            onMouseDown={(e) => e.stopPropagation()}
          >
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
          </div>
        </div>
      )}

      {draft && (
        <EventModal
          draft={draft}
          calendars={calendars}
          onSave={save}
          onDelete={draft.id ? (id) => remove(id) : undefined}
          onClose={closeDraft}
        />
      )}
    </>
  )
}
