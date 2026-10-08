import { useState } from 'react'
import DateTimeField from './DateTimeField'
import { addDays, dayKey } from '../lib/dates'
import {
  describeSpec, MAX_COUNT, MAX_INTERVAL, nthWeekdayOf, weekdayLabel, WEEKDAYS,
  type Freq, type RuleSpec, type Weekday,
} from '../lib/rrule'

const UNITS: { freq: Freq; one: string; many: string }[] = [
  { freq: 'DAILY', one: 'day', many: 'days' },
  { freq: 'WEEKLY', one: 'week', many: 'weeks' },
  { freq: 'MONTHLY', one: 'month', many: 'months' },
  { freq: 'YEARLY', one: 'year', many: 'years' },
]

const ORDINAL: Record<number, string> = { 1: 'first', 2: 'second', 3: 'third', 4: 'fourth', [-1]: 'last' }

/**
 * A whole-number input that lets the field be cleared while typing, and only reports values in
 * 1…max. Leaving it empty (or out of range) snaps back to the last good value.
 */
function CountInput({
  value, max, onChange, id, ariaLabel,
}: { value: number; max: number; onChange: (n: number) => void; id?: string; ariaLabel: string }) {
  const [text, setText] = useState(String(value))
  const [shown, setShown] = useState(value)
  if (shown !== value) {
    // The value changed from outside (another control): follow it.
    setShown(value)
    setText(String(value))
  }
  return (
    <input
      id={id}
      className="repeat-num"
      type="number"
      inputMode="numeric"
      min={1}
      max={max}
      value={text}
      aria-label={ariaLabel}
      onChange={(e) => {
        setText(e.target.value)
        const n = Number(e.target.value)
        if (Number.isInteger(n) && n >= 1 && n <= max) {
          setShown(n)
          onChange(n)
        }
      }}
      onBlur={() => setText(String(value))}
    />
  )
}

/**
 * The custom-repeat panel: every N days/weeks/months/years, on which weekdays, monthly by day or
 * by weekday-of-the-month, and when it ends. Controlled — the event editor owns the spec and turns
 * it into the RRULE, so a rule is only rewritten when something here actually changes.
 */
export default function RecurrenceEditor({
  spec,
  start,
  firstDay,
  onChange,
}: {
  spec: RuleSpec
  /** The series' first occurrence (local) — anchors "day 13" and "the second Tuesday". */
  start: Date
  /** 0 = Sunday, 1 = Monday: the order the weekday toggles are shown in. */
  firstDay: 0 | 1
  onChange: (spec: RuleSpec) => void
}) {
  const set = (patch: Partial<RuleSpec>) => onChange({ ...spec, ...patch })
  const days: Weekday[] = firstDay === 1 ? [...WEEKDAYS] : ['SU', ...WEEKDAYS.slice(0, 6)]
  const nth = nthWeekdayOf(start)
  const startDay = dayKey(start)

  const toggleDay = (d: Weekday) => {
    const next = spec.byDay.includes(d) ? spec.byDay.filter((x) => x !== d) : [...spec.byDay, d]
    if (next.length > 0) set({ byDay: next }) // a weekly rule needs at least one day
  }

  return (
    <div className="repeat-editor">
      <div className="repeat-row">
        <label htmlFor="repeat-interval">Every</label>
        <CountInput
          id="repeat-interval"
          value={spec.interval}
          max={MAX_INTERVAL}
          ariaLabel="Repeat interval"
          onChange={(interval) => set({ interval })}
        />
        <select aria-label="Repeat unit" value={spec.freq} onChange={(e) => set({ freq: e.target.value as Freq })}>
          {UNITS.map((u) => (
            <option key={u.freq} value={u.freq}>
              {spec.interval === 1 ? u.one : u.many}
            </option>
          ))}
        </select>
      </div>

      {spec.freq === 'WEEKLY' && (
        <div className="repeat-days" role="group" aria-label="On these days">
          {days.map((d) => (
            <button
              key={d}
              type="button"
              className={'repeat-day' + (spec.byDay.includes(d) ? ' active' : '')}
              aria-pressed={spec.byDay.includes(d)}
              title={weekdayLabel(d, 'long')}
              onClick={() => toggleDay(d)}
            >
              {weekdayLabel(d)}
            </button>
          ))}
        </div>
      )}

      {spec.freq === 'MONTHLY' && (
        <div className="repeat-choices" role="radiogroup" aria-label="Day of the month">
          <label className="repeat-choice">
            <input type="radio" checked={spec.monthly === 'day'} onChange={() => set({ monthly: 'day' })} />
            On day {start.getDate()}
          </label>
          <label className="repeat-choice">
            <input type="radio" checked={spec.monthly === 'weekday'} onChange={() => set({ monthly: 'weekday' })} />
            On the {ORDINAL[nth.n]} {weekdayLabel(nth.day, 'long')}
          </label>
        </div>
      )}

      <div className="repeat-choices" role="radiogroup" aria-label="Ends">
        <span className="repeat-label">Ends</span>
        <label className="repeat-choice">
          <input type="radio" checked={spec.end.kind === 'never'} onChange={() => set({ end: { kind: 'never' } })} />
          Never
        </label>
        <div className="repeat-choice">
          <input
            type="radio"
            aria-label="Ends on a date"
            checked={spec.end.kind === 'until'}
            onChange={() => set({ end: { kind: 'until', date: addDays(startDay, 90) } })}
          />
          <span>On</span>
          {spec.end.kind === 'until' && (
            <DateTimeField
              ariaLabel="Last date"
              allDay
              value={spec.end.date}
              // A series that ends before it starts is just its first occurrence; don't allow it.
              onChange={(date) => set({ end: { kind: 'until', date: date < startDay ? startDay : date } })}
            />
          )}
        </div>
        <div className="repeat-choice">
          <input
            type="radio"
            aria-label="Ends after a number of times"
            checked={spec.end.kind === 'count'}
            onChange={() => set({ end: { kind: 'count', count: 10 } })}
          />
          <span>After</span>
          {spec.end.kind === 'count' && (
            <>
              <CountInput
                value={spec.end.count}
                max={MAX_COUNT}
                ariaLabel="Number of times"
                onChange={(count) => set({ end: { kind: 'count', count } })}
              />
              <span>times</span>
            </>
          )}
        </div>
      </div>

      <p className="field-hint repeat-summary">{describeSpec(spec, start)}</p>
    </div>
  )
}
