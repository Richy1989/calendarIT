// Copy/paste of appointments. Two payloads, two jobs:
//  1. An in-app JSON snapshot in localStorage — the reliable, full-fidelity path that CalendarIT
//     pastes from. It never round-trips through .ics/import (that path is UID-idempotent, so a
//     pasted copy would be silently skipped as a duplicate); paste always creates a fresh event.
//  2. Best-effort iCalendar text on the *system* clipboard (see writeIcsToSystemClipboard) so
//     other calendar apps can paste — genuinely lossy/optional, callers must not depend on it.

import type { EventDto } from '../api/events'
import type { EventDraft } from '../EventModal'
import { addDays, toLocalInput } from './dates'

const KEY = 'calendarit.clipboard'

/** A copied event, stored in the frontend's local-string date convention: 'YYYY-MM-DD' when
 *  all-day, otherwise 'YYYY-MM-DDTHH:mm' (the same shape the event editor binds to). */
export type EventClipboard = {
  title: string
  description: string | null
  location: string | null
  categoryId: string | null
  calendarId: string | null
  allDay: boolean
  start: string
  end: string | null
  recurrence: string | null
  reminders: { minutesBefore: number; channel: string }[]
}

const listeners = new Set<() => void>()
const notify = () => listeners.forEach((l) => l())

/** Subscribe to clipboard changes — this tab's writes and other tabs (via the storage event),
 *  so a "Paste" affordance can enable itself the moment something is copied. Returns unsubscribe. */
export function subscribeClipboard(fn: () => void): () => void {
  listeners.add(fn)
  return () => { listeners.delete(fn) }
}

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (e) => { if (e.key === KEY) notify() })
}

export function hasEventClipboard(): boolean {
  try { return !!localStorage.getItem(KEY) } catch { return false }
}

/** Build a snapshot from a full event DTO. Attendees and invitation state are intentionally
 *  dropped — a paste is a personal duplicate, not a silent re-invitation of the guests. */
export function snapshotFromDto(dto: EventDto): EventClipboard {
  const allDay = dto.allDay
  return {
    title: dto.title,
    description: dto.description ?? null,
    location: dto.location ?? null,
    categoryId: dto.categoryId ?? null,
    calendarId: dto.calendarId ?? null,
    allDay,
    start: allDay ? dto.start.slice(0, 10) : toLocalInput(new Date(dto.start)),
    end: dto.end ? (allDay ? dto.end.slice(0, 10) : toLocalInput(new Date(dto.end))) : null,
    recurrence: dto.recurrence ?? null,
    reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
  }
}

export function writeEventClipboard(snap: EventClipboard): void {
  try { localStorage.setItem(KEY, JSON.stringify(snap)) } catch { /* storage blocked — paste just won't be offered */ }
  notify()
}

export function readEventClipboard(): EventClipboard | null {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return null
    const v = JSON.parse(raw) as EventClipboard
    // Guard against hand-edited / stale-shape entries.
    if (!v || typeof v.title !== 'string' || typeof v.start !== 'string' || typeof v.allDay !== 'boolean') return null
    return v
  } catch { return null }
}

/** Re-anchor a copied event onto a target day (local 'YYYY-MM-DD'), preserving the original
 *  time-of-day and duration — the way Apple/Outlook paste. Returns a draft ready to create:
 *  no id and no attendees, so it becomes a fresh personal event. Recurrence (RRULE) is carried
 *  as-is; any EXDATEs are not (they name absolute dates that re-anchoring would invalidate). */
export function pasteOnto(snap: EventClipboard, targetDay: string): EventDraft {
  const base = {
    title: snap.title,
    calendarId: snap.calendarId ?? undefined,
    categoryId: snap.categoryId,
    location: snap.location ?? '',
    description: snap.description ?? '',
    recurrence: snap.recurrence ?? '',
    reminders: snap.reminders,
    attendees: [] as EventDraft['attendees'],
  }
  if (snap.allDay) {
    const span = snap.end ? Math.max(0, daySpan(snap.start.slice(0, 10), snap.end.slice(0, 10))) : 0
    return { ...base, allDay: true, start: targetDay, end: addDays(targetDay, span) }
  }
  const timeOfDay = snap.start.slice(11) || '09:00'
  const start = `${targetDay}T${timeOfDay}`
  // Epoch-delta duration (matches how the editor drags the end when the start moves), so an
  // event that crosses a DST boundary keeps its wall-clock length rather than its exact minutes.
  const durationMs = snap.end ? new Date(snap.end).getTime() - new Date(snap.start).getTime() : 0
  const end = durationMs > 0 ? toLocalInput(new Date(new Date(start).getTime() + durationMs)) : start
  return { ...base, allDay: false, start, end }
}

/** Whole days between two 'YYYY-MM-DD' strings (inclusive-end span for all-day events). */
function daySpan(startDay: string, endDay: string): number {
  const ms = new Date(`${endDay}T00:00:00`).getTime() - new Date(`${startDay}T00:00:00`).getTime()
  return Math.round(ms / 86_400_000)
}

/** Best-effort: put standard iCalendar text on the *system* clipboard so other apps can paste
 *  it. Unlike the in-app JSON path this can silently fail — clipboard permission, or a lost
 *  user-activation on Safari once the .ics fetch resolves. We hand a Promise to ClipboardItem
 *  (Safari accepts that, keeping the write inside the gesture) and fall back to writeText. */
export async function writeIcsToSystemClipboard(fetchIcs: () => Promise<string>): Promise<void> {
  try {
    const clip = navigator.clipboard
    if (!clip) return
    if (typeof ClipboardItem !== 'undefined' && clip.write) {
      const blob = fetchIcs().then((ics) => new Blob([ics], { type: 'text/plain' }))
      await clip.write([new ClipboardItem({ 'text/plain': blob })])
    } else if (clip.writeText) {
      await clip.writeText(await fetchIcs())
    }
  } catch { /* best-effort — the in-app JSON clipboard is the reliable path */ }
}
