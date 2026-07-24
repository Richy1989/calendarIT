import { useState } from 'react'
import { dayKey, sameDay, startOfToday } from '../lib/dates'

const WEEKDAYS = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'] // Monday-first

/** First day (local midnight) of the Monday-based 6-week grid covering `month`. */
function gridStart(month: Date): Date {
  const first = new Date(month.getFullYear(), month.getMonth(), 1)
  const weekday = (first.getDay() + 6) % 7 // 0 = Monday … 6 = Sunday
  first.setDate(first.getDate() - weekday)
  first.setHours(0, 0, 0, 0)
  return first
}

/**
 * A self-contained month grid: pick a day, page months with the arrows. Purely presentational
 * — it holds only which month is on screen; the chosen date lives with the parent. Reused by
 * the date-time picker and available to anything else that needs an inline calendar.
 */
export default function MonthCalendar({
  selected,
  onSelect,
}: {
  selected: Date
  onSelect: (day: Date) => void
}) {
  const [month, setMonth] = useState(() => new Date(selected.getFullYear(), selected.getMonth(), 1))
  const today = startOfToday()
  const start = gridStart(month)
  const days = Array.from({ length: 42 }, (_, i) => {
    const d = new Date(start)
    d.setDate(start.getDate() + i)
    return d
  })

  const monthLabel = month.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
  const step = (delta: number) => setMonth((m) => new Date(m.getFullYear(), m.getMonth() + delta, 1))

  return (
    <div className="mcal">
      <div className="mcal-head">
        <button type="button" className="mcal-nav" onClick={() => step(-1)} aria-label="Previous month">
          ‹
        </button>
        <span className="mcal-title">{monthLabel}</span>
        <button type="button" className="mcal-nav" onClick={() => step(1)} aria-label="Next month">
          ›
        </button>
      </div>

      <div className="mcal-grid mcal-weekdays" aria-hidden="true">
        {WEEKDAYS.map((w) => (
          <span key={w} className="mcal-weekday">
            {w}
          </span>
        ))}
      </div>

      <div className="mcal-grid">
        {days.map((d) => {
          const inMonth = d.getMonth() === month.getMonth()
          const cls =
            'mcal-day' +
            (inMonth ? '' : ' is-muted') +
            (sameDay(d, selected) ? ' is-selected' : '') +
            (sameDay(d, today) ? ' is-today' : '')
          return (
            <button key={dayKey(d)} type="button" className={cls} onClick={() => onSelect(d)}>
              {d.getDate()}
            </button>
          )
        })}
      </div>
    </div>
  )
}
