import { describe, expect, it } from 'vitest'
import type { EventDto } from '../api/events'
import { pasteOnto, snapshotFromDto, type EventClipboard } from './eventClipboard'

// A full DTO with sane defaults; override just what a case cares about.
const makeDto = (over: Partial<EventDto>): EventDto => ({
  id: 'e1', calendarId: 'cal-a', title: 'Standup', description: null, location: null,
  categoryId: null, color: null, start: '2026-08-10T09:00:00Z', end: '2026-08-10T09:30:00Z',
  allDay: false, recurring: false, recurrence: null, reminders: [], attendees: [],
  ...over,
} as EventDto)

const clip = (over: Partial<EventClipboard>): EventClipboard => ({
  title: 'Standup', description: null, location: null, categoryId: null, calendarId: 'cal-a',
  allDay: false, start: '2026-08-10T09:00', end: '2026-08-10T09:30', recurrence: null, reminders: [],
  ...over,
})

describe('snapshotFromDto', () => {
  it('carries the editable fields and coerces reminder offsets to numbers', () => {
    const snap = snapshotFromDto(makeDto({
      description: 'notes', location: 'Room 3', categoryId: 'cat-1', recurrence: 'FREQ=WEEKLY',
      reminders: [{ minutesBefore: 15, channel: 'Email' }] as EventDto['reminders'],
    }))
    expect(snap.description).toBe('notes')
    expect(snap.location).toBe('Room 3')
    expect(snap.categoryId).toBe('cat-1')
    expect(snap.recurrence).toBe('FREQ=WEEKLY')
    expect(snap.reminders).toEqual([{ minutesBefore: 15, channel: 'Email' }])
  })

  it('never carries attendees (a paste is a personal duplicate, not a re-invite)', () => {
    const snap = snapshotFromDto(makeDto({
      attendees: [{ email: 'a@b.com', name: null, status: 'Accepted' }] as EventDto['attendees'],
    }))
    expect('attendees' in snap).toBe(false)
  })

  it('reduces an all-day event to bare date strings', () => {
    const snap = snapshotFromDto(makeDto({
      allDay: true, start: '2026-08-10T00:00:00Z', end: '2026-08-12T00:00:00Z',
    }))
    expect(snap.allDay).toBe(true)
    expect(snap.start).toBe('2026-08-10')
    expect(snap.end).toBe('2026-08-12')
  })
})

describe('pasteOnto', () => {
  it('keeps time-of-day and duration when moved to another day', () => {
    const draft = pasteOnto(clip({ start: '2026-08-10T09:00', end: '2026-08-10T10:30' }), '2026-09-01')
    expect(draft.allDay).toBe(false)
    expect(draft.start).toBe('2026-09-01T09:00')
    expect(draft.end).toBe('2026-09-01T10:30') // 90-minute duration preserved
  })

  it('preserves a multi-day span for all-day events', () => {
    const draft = pasteOnto(clip({ allDay: true, start: '2026-08-10', end: '2026-08-12' }), '2026-09-01')
    expect(draft.allDay).toBe(true)
    expect(draft.start).toBe('2026-09-01')
    expect(draft.end).toBe('2026-09-03') // 2-day span kept
  })

  it('produces a fresh draft: no id, no attendees', () => {
    const draft = pasteOnto(clip({}), '2026-09-01')
    expect(draft.id).toBeUndefined()
    expect(draft.attendees).toEqual([])
  })

  it('carries recurrence onto the pasted copy', () => {
    const draft = pasteOnto(clip({ recurrence: 'FREQ=DAILY' }), '2026-09-01')
    expect(draft.recurrence).toBe('FREQ=DAILY')
  })
})
