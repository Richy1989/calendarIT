import { useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { listEvents, type EventDto } from '../api/events'
import { dayKey, toOffsetIso } from '../lib/dates'
import { dotsByDay } from '../lib/monthDots'
import type { CalendarDto } from '../api/calendars'
import type { CategoryDto } from '../api/categories'
import MonthCalendar from './MonthCalendar'
import { UNCATEGORIZED } from '../prefs'

/** One row: a colored tick box that shows or hides, and "only" to show just this one. */
function VisibilityRow({
  label,
  color,
  checked,
  meta,
  onToggle,
  onOnly,
}: {
  label: string
  color: string
  checked: boolean
  meta?: ReactNode
  onToggle: () => void
  onOnly: () => void
}) {
  return (
    <div className="side-row">
      <button type="button" className="side-row-main" role="checkbox" aria-checked={checked} onClick={onToggle}>
        <span
          className={'side-check' + (checked ? ' on' : '')}
          style={{ ['--side-color' as string]: color }}
          aria-hidden="true"
        />
        <span className="side-row-name">{label}</span>
        {meta}
      </button>
      <button type="button" className="side-row-only" onClick={onOnly} title={`Show only ${label}`}>
        only
      </button>
    </div>
  )
}

/**
 * The calendar's left sidebar (desktop): the brand and a collapse button, "New", a month to jump
 * around in, and what is shown — the user's calendars, their subscriptions and the categories,
 * each with a tick box. Collapsing hands the whole width to the grid.
 */
export default function Sidebar({
  brand,
  onCollapse,
  onNew,
  tasksOpen,
  onToggleTasks,
  selectedDate,
  onPickDate,
  calendars,
  categories,
  calendarColor,
  isCalVisible,
  onToggleCal,
  onOnlyCal,
  isCatVisible,
  onToggleCat,
  onOnlyCat,
  onManageCalendars,
  onManageCategories,
  isShown,
}: {
  brand: ReactNode
  onCollapse: () => void
  onNew: () => void
  /** The task panel: whether it is open, and the button that opens or closes it. */
  tasksOpen: boolean
  onToggleTasks: () => void
  selectedDate: Date
  onPickDate: (day: Date) => void
  calendars: CalendarDto[]
  categories: CategoryDto[]
  calendarColor: (c: CalendarDto) => string
  isCalVisible: (id: string) => boolean
  onToggleCal: (id: string) => void
  onOnlyCal: (id: string) => void
  isCatVisible: (id: string) => boolean
  onToggleCat: (id: string) => void
  onOnlyCat: (id: string) => void
  onManageCalendars?: () => void
  onManageCategories?: () => void
  /** Whether an appointment is shown in the grid (its calendar and category ticked). */
  isShown: (dto: EventDto) => boolean
}) {
  // The appointments of the month on screen, for the dots under its days.
  const [range, setRange] = useState<{ from: string; to: string } | null>(null)
  const { data: events = [] } = useQuery({
    queryKey: ['events', range?.from, range?.to],
    queryFn: () => listEvents(range!.from, range!.to),
    enabled: !!range,
    staleTime: 30_000,
  })
  const dots = useMemo(() => dotsByDay(events.filter(isShown)), [events, isShown])
  const own = calendars.filter((c) => !c.subscription)
  const subscribed = calendars.filter((c) => c.subscription)
  const monthKey = `${selectedDate.getFullYear()}-${selectedDate.getMonth()}`

  const calendarRow = (c: CalendarDto) => (
    <VisibilityRow
      key={c.id}
      label={c.name}
      color={calendarColor(c)}
      checked={isCalVisible(c.id)}
      meta={c.subscription?.lastError ? <span className="side-row-warn" title={c.subscription.lastError}>!</span> : null}
      onToggle={() => onToggleCal(c.id)}
      onOnly={() => onOnlyCal(c.id)}
    />
  )

  return (
    <aside className="cal-sidebar" aria-label="Calendars">
      <div className="cal-sidebar-head">
        {brand}
        <button type="button" className="side-icon-btn" onClick={onCollapse} aria-label="Hide sidebar" title="Hide sidebar">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <rect x="3" y="4" width="18" height="16" rx="2.5" />
            <path d="M9 4v16" />
            <path d="M15.5 10l-2 2 2 2" />
          </svg>
        </button>
      </div>

      <div className="side-actions">
        <button type="button" className="side-new" onClick={onNew}>
          ＋ New appointment
        </button>
        <button
          type="button"
          className={'side-new side-tasks' + (tasksOpen ? ' is-on' : '')}
          aria-pressed={tasksOpen}
          onClick={onToggleTasks}
        >
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <rect x="4" y="4" width="16" height="16" rx="3" />
            <path d="M8.5 12l2.5 2.5 4.5-5" />
          </svg>
          Tasks
        </button>
      </div>

      {/* Re-keyed per month, so it follows when the grid pages to another one. */}
      <div className="side-month">
        <MonthCalendar
          key={monthKey}
          selected={selectedDate}
          onSelect={onPickDate}
          dayDots={(day) => dots.get(dayKey(day)) ?? []}
          onRangeChange={(from, to) => setRange({ from: toOffsetIso(from), to: toOffsetIso(to) })}
        />
      </div>

      <div className="side-scroll">
        <section className="side-group">
          <h3>My calendars</h3>
          {own.map(calendarRow)}
        </section>

        {subscribed.length > 0 && (
          <section className="side-group">
            <h3>Subscriptions</h3>
            {subscribed.map(calendarRow)}
          </section>
        )}

        {onManageCalendars && (
          <button type="button" className="side-link" onClick={onManageCalendars}>
            Manage or subscribe…
          </button>
        )}

        {categories.length > 0 && (
          <section className="side-group">
            <h3>Categories</h3>
            {categories.map((c) => (
              <VisibilityRow
                key={c.id}
                label={c.name}
                color={c.color}
                checked={isCatVisible(c.id)}
                onToggle={() => onToggleCat(c.id)}
                onOnly={() => onOnlyCat(c.id)}
              />
            ))}
            <VisibilityRow
              label="Uncategorized"
              color="var(--text-faint)"
              checked={isCatVisible(UNCATEGORIZED)}
              onToggle={() => onToggleCat(UNCATEGORIZED)}
              onOnly={() => onOnlyCat(UNCATEGORIZED)}
            />
            {onManageCategories && (
              <button type="button" className="side-link" onClick={onManageCategories}>
                Manage categories…
              </button>
            )}
          </section>
        )}
      </div>
    </aside>
  )
}
