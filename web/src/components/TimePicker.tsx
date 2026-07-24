import { useEffect, useRef, useState } from 'react'
import { useHour12 } from '../clock'
import { pad2 } from '../lib/dates'

/** Returns a copy of `base` with the given 24h hour and minute. */
function withTime(base: Date, h24: number, minute: number): Date {
  const d = new Date(base)
  d.setHours(h24, minute, 0, 0)
  return d
}

/** An editable, stepper-driven number field (hours or minutes). Commits on blur/Enter so
 *  typing isn't fought by re-clamping mid-keystroke; the ▲/▼ steppers wrap around. */
function Spin({
  value,
  onCommit,
  onStep,
  label,
}: {
  value: number
  onCommit: (raw: string) => void
  onStep: (delta: number) => void
  label: string
}) {
  const [buffer, setBuffer] = useState(pad2(value))
  const focused = useRef(false)

  // Keep the field in sync with the source of truth, except while the user is mid-edit.
  useEffect(() => {
    if (!focused.current) setBuffer(pad2(value))
  }, [value])

  const commit = () => {
    onCommit(buffer)
    setBuffer(pad2(value))
  }

  return (
    <div className="tp-spin">
      <button type="button" className="tp-step" onClick={() => onStep(1)} aria-label={`${label} up`}>
        ▲
      </button>
      <input
        className="tp-num"
        inputMode="numeric"
        maxLength={2}
        aria-label={label}
        value={buffer}
        onFocus={(e) => {
          focused.current = true
          e.target.select()
        }}
        onChange={(e) => setBuffer(e.target.value.replace(/\D/g, ''))}
        onBlur={() => {
          focused.current = false
          commit()
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur()
          if (e.key === 'ArrowUp') {
            e.preventDefault()
            onStep(1)
          }
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            onStep(-1)
          }
        }}
      />
      <button type="button" className="tp-step" onClick={() => onStep(-1)} aria-label={`${label} down`}>
        ▼
      </button>
    </div>
  )
}

/**
 * Hour + minute picker that follows the app's 12h/24h preference: 24-hour shows 0–23, while
 * 12-hour shows 1–12 with an AM/PM toggle. Minutes step by 5 but any value can be typed.
 * Presentational — the chosen instant lives with the parent.
 */
export default function TimePicker({ date, onChange }: { date: Date; onChange: (d: Date) => void }) {
  const hour12 = useHour12()
  const h24 = date.getHours()
  const minute = date.getMinutes()
  const isPM = h24 >= 12
  const displayHour = hour12 ? ((h24 + 11) % 12) + 1 : h24

  // Map a display hour (1–12 in 12h mode, 0–23 in 24h) back to a 24-hour hour.
  const toH24 = (display: number) => (hour12 ? (display % 12) + (isPM ? 12 : 0) : display)

  const stepHour = (delta: number) => {
    const next = hour12 ? ((displayHour - 1 + delta + 12) % 12) + 1 : (h24 + delta + 24) % 24
    onChange(withTime(date, toH24(next), minute))
  }
  const commitHour = (raw: string) => {
    const n = parseInt(raw, 10)
    if (Number.isNaN(n)) return
    const clamped = hour12 ? Math.min(12, Math.max(1, n)) : Math.min(23, Math.max(0, n))
    onChange(withTime(date, toH24(clamped), minute))
  }

  const stepMinute = (delta: number) => onChange(withTime(date, h24, (minute + delta * 5 + 60) % 60))
  const commitMinute = (raw: string) => {
    const n = parseInt(raw, 10)
    if (Number.isNaN(n)) return
    onChange(withTime(date, h24, Math.min(59, Math.max(0, n))))
  }

  const setMeridiem = (pm: boolean) => {
    if (pm === isPM) return
    onChange(withTime(date, (h24 % 12) + (pm ? 12 : 0), minute))
  }

  return (
    <div className="tp">
      <Spin value={displayHour} onCommit={commitHour} onStep={stepHour} label="Hours" />
      <span className="tp-colon">:</span>
      <Spin value={minute} onCommit={commitMinute} onStep={stepMinute} label="Minutes" />
      {hour12 && (
        <div className="tp-meridiem">
          <button type="button" className={'tp-mer' + (!isPM ? ' is-on' : '')} onClick={() => setMeridiem(false)}>
            AM
          </button>
          <button type="button" className={'tp-mer' + (isPM ? ' is-on' : '')} onClick={() => setMeridiem(true)}>
            PM
          </button>
        </div>
      )}
    </div>
  )
}
