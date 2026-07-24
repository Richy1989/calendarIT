import { useRef, useState } from 'react'
import { useHour12 } from '../clock'
import { formatDateCompact, formatTime, parseLocalValue, toLocalValue } from '../lib/dates'
import { anchorBelow, Popover, type Anchor } from './Popover'
import MonthCalendar from './MonthCalendar'
import TimePicker from './TimePicker'

/**
 * The app's date (and optional time) picker: a styled trigger showing the current value, which
 * opens a popover with an inline month calendar and — unless `allDay` — a 12/24h-aware time
 * picker. It reads and writes the same local strings the native input did ('YYYY-MM-DD' for
 * all-day, 'YYYY-MM-DDTHH:mm' for timed), so it drops in wherever one was used, and unlike the
 * native control it matches the app's look and honors the user's clock-format preference.
 */
export default function DateTimeField({
  value,
  onChange,
  allDay,
  id,
  ariaLabel,
}: {
  value: string
  onChange: (value: string) => void
  allDay: boolean
  id?: string
  ariaLabel?: string
}) {
  const hour12 = useHour12()
  const [anchor, setAnchor] = useState<Anchor | null>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)

  const date = parseLocalValue(value, allDay)
  const emit = (d: Date) => onChange(toLocalValue(d, allDay))

  // Picking a day keeps the time of day; changing the time keeps the day.
  const pickDay = (day: Date) => {
    const next = new Date(day)
    next.setHours(date.getHours(), date.getMinutes(), 0, 0)
    emit(next)
  }

  const toggle = () => {
    setAnchor((open) => (open ? null : triggerRef.current ? anchorBelow(triggerRef.current) : null))
  }

  return (
    <>
      <button
        type="button"
        ref={triggerRef}
        id={id}
        aria-label={ariaLabel}
        className={'dtf-trigger' + (anchor ? ' is-open' : '')}
        onClick={toggle}
      >
        <CalendarGlyph />
        {/* Date shrinks (ellipsis) before the time does — the time must never wrap or clip. */}
        <span className="dtf-date">{formatDateCompact(date)}</span>
        {!allDay && <span className="dtf-time">{formatTime(date, hour12)}</span>}
      </button>

      {anchor && (
        <Popover anchor={anchor} onClose={() => setAnchor(null)} className="dtf-pop">
          <MonthCalendar selected={date} onSelect={pickDay} />
          {!allDay && (
            <div className="dtf-time-row">
              <TimePicker date={date} onChange={emit} />
            </div>
          )}
        </Popover>
      )}
    </>
  )
}

const glyph = { width: 15, height: 15, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }

function CalendarGlyph() {
  return (
    <svg {...glyph} className="dtf-icon" aria-hidden="true">
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <path d="M16 2v4M8 2v4M3 10h18" />
    </svg>
  )
}

