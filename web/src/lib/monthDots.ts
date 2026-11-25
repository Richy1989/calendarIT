// The colored dots a month overview puts under its days: which appointments fall on each.

import type { EventDto } from '../api/events'
import { allDayDate, dayKey } from './dates'

const MAX_DOTS = 3
const DEFAULT_COLOR = '#708090'

/** Each day's appointment colors (distinct, in start order, at most three), multi-day ones on
 *  every day they cover. */
export function dotsByDay(events: EventDto[]): Map<string, string[]> {
  const byDay = new Map<string, string[]>()
  const sorted = [...events].sort((a, b) => a.start.localeCompare(b.start))
  for (const e of sorted) {
    const color = e.color ?? DEFAULT_COLOR
    const first = e.allDay ? allDayDate(e.start) : new Date(e.start)
    // A timed appointment ending at midnight doesn't reach into the next day.
    const last = e.allDay
      ? allDayDate(e.end ?? e.start)
      : new Date(Math.max(new Date(e.end ?? e.start).getTime() - 1, first.getTime()))
    const day = new Date(first.getFullYear(), first.getMonth(), first.getDate())
    for (let i = 0; i < 42 && day <= last; i++, day.setDate(day.getDate() + 1)) {
      const key = dayKey(day)
      const colors = byDay.get(key) ?? []
      if (colors.length < MAX_DOTS && !colors.includes(color)) colors.push(color)
      byDay.set(key, colors)
    }
  }
  return byDay
}

