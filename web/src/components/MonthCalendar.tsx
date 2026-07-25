import { useState } from 'react'
import { dayKey, sameDay, startOfToday } from '../lib/dates'
import { useFirstDay, type FirstDay } from '../weekStart'

// Sunday-first, so a day's own getDay() indexes straight into it; the header is rotated to
// whichever day the user starts their week on.
const WEEKDAYS = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa']

const weekdayLabels = (firstDay: FirstDay) => [...WEEKDAYS.slice(firstDay), ...WEEKDAYS.slice(0, firstDay)]

/** First day (local midnight) of the 6-week grid covering `month`, starting on `firstDay`. */
function gridStart(month: Date, firstDay: FirstDay): Date {
  const first = new Date(month.getFullYear(), month.getMonth(), 1)
  const offset = (first.getDay() - firstDay + 7) % 7 // days back to the start of that week
  first.setDate(first.getDate() - offset)
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
  const firstDay = useFirstDay()
  const start = gridStart(month, firstDay)
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
        {weekdayLabels(firstDay).map((w) => (
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
