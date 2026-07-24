// Shared date/time helpers. One home for the little conversions the calendar, agenda,
// search, and the date-time picker all need — so the format conventions stay in lock-step.

export const pad2 = (n: number) => String(n).padStart(2, '0')

/** A Date → local 'YYYY-MM-DD' (the all-day value + API date convention). */
export function dayKey(d: Date): string {
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`
}

/** A Date → local 'YYYY-MM-DDTHH:mm' (the timed value the event modal binds to). */
export function toLocalInput(d: Date): string {
  return `${dayKey(d)}T${pad2(d.getHours())}:${pad2(d.getMinutes())}`
}

/** Shifts a 'YYYY-MM-DD' day string by n days (e.g. FullCalendar's exclusive all-day end). */
export function addDays(day: string, n: number): string {
  const d = new Date(`${day}T00:00:00`)
  d.setDate(d.getDate() + n)
  return dayKey(d)
}

/** Today at local midnight. */
export function startOfToday(): Date {
  const d = new Date()
  d.setHours(0, 0, 0, 0)
  return d
}

/** True when two dates fall on the same local calendar day. */
export function sameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}

/** Time of day, honoring the 12h/24h preference. Pass the caller's `useHour12()` value. */
export function formatTime(d: Date, hour12: boolean, withSeconds = false): string {
  return d.toLocaleTimeString(undefined, {
    hour: 'numeric',
    minute: '2-digit',
    ...(withSeconds ? { second: '2-digit' as const } : {}),
    hour12,
  })
}

/** A compact, human date like "Mon, Sep 1 2026". */
export function formatDateMedium(d: Date): string {
  return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' })
}

/**
 * Parses the event modal's date strings into a local Date. All-day values are
 * 'YYYY-MM-DD'; timed values are 'YYYY-MM-DDTHH:mm'. Anything unparseable falls back to
 * today (all-day) or the next hour (timed), so the picker always opens on something sane.
 */
export function parseLocalValue(value: string, allDay: boolean): Date {
  const parsed = allDay ? new Date(`${value.slice(0, 10)}T00:00:00`) : new Date(value)
  if (!Number.isNaN(parsed.getTime())) return parsed
  const now = new Date()
  if (allDay) now.setHours(0, 0, 0, 0)
  else now.setMinutes(0, 0, 0)
  return now
}

/** Serializes a Date back to the modal's string convention for the given mode. */
export function toLocalValue(d: Date, allDay: boolean): string {
  return allDay ? dayKey(d) : toLocalInput(d)
}
