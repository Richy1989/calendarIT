import { dayKey, formatDateMedium } from './dates'

/**
 * The repeat rules the custom-repeat editor can write and read back: "every N days / weeks /
 * months / years", on chosen weekdays, monthly on a day or on "the second Tuesday", ending never,
 * on a date, or after N times — i.e. the RRULE parts FREQ, INTERVAL, BYDAY, COUNT and UNTIL.
 *
 * Anything else (a rule a phone or another app wrote, with BYSETPOS or several BYMONTHDAYs, say)
 * parses to null and is left exactly as it is: the editor never rewrites a rule it can't fully
 * represent. And a rule is only ever rewritten when the user changes it — the server treats a
 * changed rule as a moved series and drops its exceptions, so "open and save" must be a no-op.
 */

export const WEEKDAYS = ['MO', 'TU', 'WE', 'TH', 'FR', 'SA', 'SU'] as const
export type Weekday = (typeof WEEKDAYS)[number]
export type Freq = 'DAILY' | 'WEEKLY' | 'MONTHLY' | 'YEARLY'

export type RuleEnd = { kind: 'never' } | { kind: 'until'; date: string } | { kind: 'count'; count: number }

export type RuleSpec = {
  freq: Freq
  /** Every `interval` days/weeks/…; 1 or more. */
  interval: number
  /** Weekly only: the weekdays it falls on. */
  byDay: Weekday[]
  /** Monthly only: on the start's day of the month, or on its weekday-of-the-month ("2nd Tue"). */
  monthly: 'day' | 'weekday'
  end: RuleEnd
}

export const MAX_INTERVAL = 999
export const MAX_COUNT = 999

const JS_DAY: Weekday[] = ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA']

export function weekdayOf(d: Date): Weekday {
  return JS_DAY[d.getDay()]
}

/** Which of its weekday the date is in its month: 1–4, or -1 for a fifth (= the last). */
export function nthWeekdayOf(d: Date): { n: number; day: Weekday } {
  const n = Math.ceil(d.getDate() / 7)
  return { n: n >= 5 ? -1 : n, day: weekdayOf(d) }
}

/** A sensible starting point for the editor: weekly on the start's weekday, never ending. */
export function defaultSpec(start: Date, freq: Freq = 'WEEKLY'): RuleSpec {
  return { freq, interval: 1, byDay: [weekdayOf(start)], monthly: 'day', end: { kind: 'never' } }
}

function parseParts(rule: string): Map<string, string> | null {
  const text = rule.trim().replace(/^RRULE:/i, '')
  if (!text) return null
  const parts = new Map<string, string>()
  for (const piece of text.split(';')) {
    if (!piece) continue
    const eq = piece.indexOf('=')
    if (eq <= 0) return null
    parts.set(piece.slice(0, eq).trim().toUpperCase(), piece.slice(eq + 1).trim().toUpperCase())
  }
  return parts
}

/** UNTIL (DATE or UTC DATE-TIME) → the local calendar day it falls on, 'YYYY-MM-DD'. */
function untilToDay(value: string): string | null {
  const m = /^(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})(Z)?)?$/.exec(value)
  if (!m) return null
  if (!m[4]) return `${m[1]}-${m[2]}-${m[3]}`
  const [y, mo, d, h, mi, s] = m.slice(1, 7).map(Number)
  const when = m[7] ? new Date(Date.UTC(y, mo - 1, d, h, mi, s)) : new Date(y, mo - 1, d, h, mi, s)
  return dayKey(when)
}

/** Reads a rule into an editable spec, or null when it uses anything the editor doesn't model. */
export function parseRule(rule: string, start: Date): RuleSpec | null {
  const parts = parseParts(rule)
  if (!parts) return null
  const allowed = new Set(['FREQ', 'INTERVAL', 'BYDAY', 'COUNT', 'UNTIL', 'WKST', 'BYMONTHDAY', 'BYMONTH'])
  if ([...parts.keys()].some((k) => !allowed.has(k))) return null

  const freq = parts.get('FREQ')
  if (freq !== 'DAILY' && freq !== 'WEEKLY' && freq !== 'MONTHLY' && freq !== 'YEARLY') return null
  const spec = defaultSpec(start, freq)

  const interval = parts.get('INTERVAL')
  if (interval !== undefined) {
    if (!/^\d+$/.test(interval) || Number(interval) < 1 || Number(interval) > MAX_INTERVAL) return null
    spec.interval = Number(interval)
  }

  // BYMONTHDAY / BYMONTH only when they just restate the start (some apps spell that out).
  const monthDay = parts.get('BYMONTHDAY')
  if (monthDay !== undefined && (freq === 'DAILY' || freq === 'WEEKLY' || Number(monthDay) !== start.getDate())) return null
  const month = parts.get('BYMONTH')
  if (month !== undefined && (freq !== 'YEARLY' || Number(month) !== start.getMonth() + 1)) return null

  const byDay = parts.get('BYDAY')
  if (byDay !== undefined) {
    if (freq === 'WEEKLY') {
      const days = byDay.split(',')
      if (!days.every((d): d is Weekday => (WEEKDAYS as readonly string[]).includes(d))) return null
      spec.byDay = WEEKDAYS.filter((d) => days.includes(d))
    } else if (freq === 'MONTHLY' && monthDay === undefined) {
      const m = /^(-1|[1-4])(MO|TU|WE|TH|FR|SA|SU)$/.exec(byDay)
      const nth = nthWeekdayOf(start)
      if (!m || Number(m[1]) !== nth.n || m[2] !== nth.day) return null
      spec.monthly = 'weekday'
    } else {
      return null
    }
  }

  const count = parts.get('COUNT')
  const until = parts.get('UNTIL')
  if (count !== undefined && until !== undefined) return null
  if (count !== undefined) {
    if (!/^\d+$/.test(count) || Number(count) < 1 || Number(count) > MAX_COUNT) return null
    spec.end = { kind: 'count', count: Number(count) }
  } else if (until !== undefined) {
    const day = untilToDay(until)
    if (!day) return null
    spec.end = { kind: 'until', date: day }
  }
  return spec
}

const pad = (n: number, w = 2) => String(n).padStart(w, '0')

/**
 * The RRULE for a spec, anchored on `start` (local). Written as plainly as possible — no INTERVAL=1,
 * no BYDAY that only repeats the start's weekday — so the presets stay recognisable. UNTIL is a
 * DATE for an all-day series (as RFC 5545 requires next to a DATE start) and otherwise the end of
 * that local day in UTC, so the last day's occurrence is included.
 */
export function buildRule(spec: RuleSpec, start: Date, allDay: boolean): string {
  const parts = [`FREQ=${spec.freq}`]
  const interval = Math.min(Math.max(1, Math.floor(spec.interval) || 1), MAX_INTERVAL)
  if (interval > 1) parts.push(`INTERVAL=${interval}`)

  if (spec.freq === 'WEEKLY') {
    const days = WEEKDAYS.filter((d) => spec.byDay.includes(d))
    const own = weekdayOf(start)
    if (days.length > 0 && !(days.length === 1 && days[0] === own)) parts.push(`BYDAY=${days.join(',')}`)
  } else if (spec.freq === 'MONTHLY' && spec.monthly === 'weekday') {
    const { n, day } = nthWeekdayOf(start)
    parts.push(`BYDAY=${n}${day}`)
  }

  if (spec.end.kind === 'count') {
    parts.push(`COUNT=${Math.min(Math.max(1, Math.floor(spec.end.count) || 1), MAX_COUNT)}`)
  } else if (spec.end.kind === 'until') {
    const [y, m, d] = spec.end.date.split('-').map(Number)
    if (allDay) {
      parts.push(`UNTIL=${pad(y, 4)}${pad(m)}${pad(d)}`)
    } else {
      const end = new Date(y, m - 1, d, 23, 59, 59)
      parts.push(
        `UNTIL=${pad(end.getUTCFullYear(), 4)}${pad(end.getUTCMonth() + 1)}${pad(end.getUTCDate())}` +
          `T${pad(end.getUTCHours())}${pad(end.getUTCMinutes())}${pad(end.getUTCSeconds())}Z`,
      )
    }
  }
  return parts.join(';')
}

/** Short weekday name ("Mon") for a weekday code, in the user's locale. */
export function weekdayLabel(day: Weekday, style: 'short' | 'long' = 'short'): string {
  // 1 Jan 2024 was a Monday.
  return new Date(2024, 0, 1 + WEEKDAYS.indexOf(day)).toLocaleDateString(undefined, { weekday: style })
}

const ORDINAL: Record<number, string> = { 1: 'first', 2: 'second', 3: 'third', 4: 'fourth', [-1]: 'last' }
const UNIT: Record<Freq, [string, string]> = {
  DAILY: ['day', 'days'],
  WEEKLY: ['week', 'weeks'],
  MONTHLY: ['month', 'months'],
  YEARLY: ['year', 'years'],
}

/** "Every 4 weeks on Mon, Tue · until Dec 31, 2026" — or "Custom rule" for one the editor can't read. */
export function describeRule(rule: string, start: Date): string {
  const spec = parseRule(rule, start)
  if (!spec) return 'Custom rule'
  return describeSpec(spec, start)
}

export function describeSpec(spec: RuleSpec, start: Date): string {
  const [one, many] = UNIT[spec.freq]
  let text = spec.interval > 1 ? `Every ${spec.interval} ${many}` : `Every ${one}`
  if (spec.freq === 'WEEKLY') {
    const days = WEEKDAYS.filter((d) => spec.byDay.includes(d))
    const shown = days.length > 0 ? days : [weekdayOf(start)]
    const weekdaysOnly = shown.length === 5 && !shown.includes('SA') && !shown.includes('SU')
    text += weekdaysOnly ? ' on weekdays' : ` on ${shown.map((d) => weekdayLabel(d)).join(', ')}`
  } else if (spec.freq === 'MONTHLY') {
    if (spec.monthly === 'weekday') {
      const { n, day } = nthWeekdayOf(start)
      text += ` on the ${ORDINAL[n]} ${weekdayLabel(day, 'long')}`
    } else {
      text += ` on day ${start.getDate()}`
    }
  } else if (spec.freq === 'YEARLY') {
    text += ` on ${start.toLocaleDateString(undefined, { month: 'long', day: 'numeric' })}`
  }
  if (spec.end.kind === 'count') {
    text += spec.end.count === 1 ? ' · once' : ` · ${spec.end.count} times`
  } else if (spec.end.kind === 'until') {
    text += ` · until ${formatDateMedium(new Date(`${spec.end.date}T00:00:00`))}`
  }
  return text
}
