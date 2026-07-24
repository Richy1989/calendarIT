import { useEffect, useRef, useState } from 'react'
import { useInfiniteQuery } from '@tanstack/react-query'
import { listEvents, type EventDto } from './api/events'
import { useHour12 } from './clock'
import { dayKey, formatDateMedium, formatTime, startOfToday } from './lib/dates'
import { UNCATEGORIZED } from './prefs'

/**
 * The "list" view: every upcoming appointment from today forward, in one scrollable
 * list. Loaded lazily in 3-month windows — scrolling to the bottom fetches the next
 * window (auto-repeating through empty stretches) up to a 5-year horizon, so nothing
 * is ever loaded wholesale. Recurring series appear as their expanded occurrences.
 */
const WINDOW_MONTHS = 3
const MAX_WINDOWS = 20 // 20 × 3 months = 5 years of scrolling

const DEFAULT_COLOR = '#708090' // slategray — uncategorized

function windowStart(index: number): Date {
  const d = startOfToday()
  d.setMonth(d.getMonth() + index * WINDOW_MONTHS)
  return d
}

export default function AgendaView({
  visibleCalendarIds,
  visibleCategoryIds,
  calPickerLabel,
  catPickerLabel,
  onOpenCalPicker,
  onOpenCatPicker,
  onNew,
  onExit,
  onEdit,
  onEventContext,
}: {
  visibleCalendarIds?: string[] | null
  visibleCategoryIds?: string[] | null
  calPickerLabel: string
  catPickerLabel: string
  /** Open the calendar/category visibility popover, anchored to the given button. */
  onOpenCalPicker: (rect: DOMRect) => void
  onOpenCatPicker: (rect: DOMRect) => void
  onNew: () => void
  /** Leave the list for a grid view (month/week/day). */
  onExit: (view: 'dayGridMonth' | 'timeGridWeek' | 'timeGridDay') => void
  onEdit: (seriesId: string) => void
  onEventContext: (x: number, y: number, e: { seriesId: string; recurring: boolean; occurrenceStart: string }) => void
}) {
  const hour12 = useHour12()
  // Windows are keyed off today so the list stays correct across midnight.
  const todayKey = dayKey(startOfToday())
  const { data, hasNextPage, isFetchingNextPage, fetchNextPage } = useInfiniteQuery({
    queryKey: ['events', 'agenda', todayKey],
    queryFn: ({ pageParam }) =>
      listEvents(windowStart(pageParam).toISOString(), windowStart(pageParam + 1).toISOString()),
    initialPageParam: 0,
    getNextPageParam: (_last, pages) => (pages.length < MAX_WINDOWS ? pages.length : undefined),
  })

  // Windows are disjoint and each arrives sorted, so the concatenation is sorted too.
  // Events spanning a window boundary show up in both windows — dedupe by id + start.
  const seen = new Set<string>()
  const events: EventDto[] = []
  for (const page of data?.pages ?? []) {
    for (const dto of page) {
      const key = `${dto.id}__${dto.start}`
      if (seen.has(key)) continue
      seen.add(key)
      if (dto.invitationStatus === 'Declined') continue // a declined invitation drops off the list
      if (visibleCalendarIds && !visibleCalendarIds.includes(dto.calendarId)) continue
      if (visibleCategoryIds && !visibleCategoryIds.includes(dto.categoryId ?? UNCATEGORIZED)) continue
      events.push(dto)
    }
  }

  // Load the next window whenever the bottom sentinel is on screen — re-checked after
  // every page so empty stretches (or a filter hiding a whole window) keep auto-loading.
  const sentinelRef = useRef<HTMLDivElement>(null)
  const [sentinelInView, setSentinelInView] = useState(false)
  useEffect(() => {
    const el = sentinelRef.current
    if (!el) return
    const obs = new IntersectionObserver((entries) => setSentinelInView(entries[0].isIntersecting))
    obs.observe(el)
    return () => obs.disconnect()
  }, [])
  useEffect(() => {
    if (sentinelInView && hasNextPage && !isFetchingNextPage) fetchNextPage()
  }, [sentinelInView, hasNextPage, isFetchingNextPage, fetchNextPage, data])

  // Group into day sections for rendering.
  const days: { key: string; label: string; items: EventDto[] }[] = []
  for (const dto of events) {
    const start = new Date(dto.start)
    const key = dto.allDay ? dto.start.slice(0, 10) : dayKey(start)
    const last = days[days.length - 1]
    if (last?.key === key) {
      last.items.push(dto)
    } else {
      const label = dto.allDay ? formatDateMedium(new Date(`${key}T00:00:00`)) : formatDateMedium(start)
      days.push({ key, label, items: [dto] })
    }
  }

  const timeLabel = (dto: EventDto) => {
    if (dto.allDay) return 'all-day'
    const start = formatTime(new Date(dto.start), hour12)
    return dto.end ? `${start} – ${formatTime(new Date(dto.end), hour12)}` : start
  }

  return (
    <div className="fc agenda">
      {/* Same toolbar anatomy (and CSS) as the FullCalendar views, so nothing jumps. */}
      <div className="fc-header-toolbar fc-toolbar">
        <div className="fc-toolbar-chunk">
          <button type="button" className="fc-button fc-button-primary fc-addEvent-button" onClick={onNew}>
            +  New
          </button>
          <button
            type="button"
            className="fc-button fc-button-primary"
            onClick={(e) => onOpenCalPicker(e.currentTarget.getBoundingClientRect())}
          >
            {calPickerLabel}
          </button>
          <button
            type="button"
            className="fc-button fc-button-primary"
            onClick={(e) => onOpenCatPicker(e.currentTarget.getBoundingClientRect())}
          >
            {catPickerLabel}
          </button>
        </div>
        <div className="fc-toolbar-chunk">
          <h2 className="agenda-title">Upcoming</h2>
        </div>
        <div className="fc-toolbar-chunk">
          <div className="fc-button-group">
            <button type="button" className="fc-button fc-button-primary" onClick={() => onExit('dayGridMonth')}>
              month
            </button>
            <button type="button" className="fc-button fc-button-primary" onClick={() => onExit('timeGridWeek')}>
              week
            </button>
            <button type="button" className="fc-button fc-button-primary" onClick={() => onExit('timeGridDay')}>
              day
            </button>
            <button type="button" className="fc-button fc-button-primary fc-button-active">
              list
            </button>
          </div>
        </div>
      </div>

      <div className="agenda-body">
        {days.map((day) => (
          <section key={day.key}>
            <div className="agenda-day">{day.label}</div>
            {day.items.map((dto) => (
              <button
                type="button"
                className="agenda-row"
                key={`${dto.id}__${dto.start}`}
                onClick={() => onEdit(dto.id)}
                onContextMenu={(e) => {
                  e.preventDefault()
                  onEventContext(e.clientX, e.clientY, {
                    seriesId: dto.id,
                    recurring: dto.recurring,
                    occurrenceStart: dto.start,
                  })
                }}
              >
                <span className="agenda-time">{timeLabel(dto)}</span>
                <span className="agenda-dot" style={{ background: dto.color ?? DEFAULT_COLOR }} aria-hidden="true" />
                <span className="agenda-row-title">
                  {dto.title}
                  {dto.recurring && <span className="agenda-recurring" title="Repeats"> ⟳</span>}
                </span>
                {dto.location && <span className="agenda-row-loc">{dto.location}</span>}
              </button>
            ))}
          </section>
        ))}

        {events.length === 0 && !hasNextPage && !isFetchingNextPage && (
          <p className="agenda-note">No upcoming appointments match the current filter.</p>
        )}

        <div ref={sentinelRef} className="agenda-sentinel" aria-hidden="true" />
        {isFetchingNextPage && <p className="agenda-note">Loading more…</p>}
        {!hasNextPage && events.length > 0 && (
          <p className="agenda-note">That's everything for the next 5 years.</p>
        )}
      </div>
    </div>
  )
}
