import { describe, expect, it } from 'vitest'
import type { EventDto, SaveEventRequest } from '../api/events'
import { canUndoDelete, dtoToRequest, eventEditUndo, occurrenceEditUndo, type EventWrites } from './eventOps'

const body = (title: string): SaveEventRequest =>
  ({ title, start: '2026-11-02T08:00:00Z', end: null, allDay: false, recurrence: null, reminders: null }) as SaveEventRequest

function recorder() {
  const calls: string[] = []
  const writes: EventWrites = {
    updateEvent: async (id, b) => calls.push(`update ${id} ${b.title}`),
    upsertOccurrence: async (s, o, b) => calls.push(`upsert ${s}@${o} ${b.title}`),
    resetOccurrence: async (s, o) => calls.push(`reset ${s}@${o}`),
  }
  return { calls, writes }
}

const ref = { seriesId: 's', occurrence: '2026-11-04T08:00:00Z' }

describe('occurrenceEditUndo (editor and drag share it)', () => {
  it('undoes an occurrence without own edits back to the series', async () => {
    const { calls, writes } = recorder()
    const entry = occurrenceEditUndo(writes, { ...ref, hadOwnEdits: false }, body('ignored'), body('moved'))!
    await entry.undo()
    await entry.redo()
    expect(calls).toEqual(['reset s@2026-11-04T08:00:00Z', 'upsert s@2026-11-04T08:00:00Z moved'])
  })

  it('undoes an already-edited occurrence back to its earlier edits, not to the series', async () => {
    const { calls, writes } = recorder()
    const entry = occurrenceEditUndo(writes, { ...ref, hadOwnEdits: true }, body('earlier edit'), body('moved'))!
    await entry.undo()
    expect(calls).toEqual(['upsert s@2026-11-04T08:00:00Z earlier edit'])
  })

  it('offers no undo when an edited occurrence’s earlier state is unknown (a reset would lose it)', () => {
    const { writes } = recorder()
    expect(occurrenceEditUndo(writes, { ...ref, hadOwnEdits: true }, null, body('moved'))).toBeNull()
  })
})

describe('eventEditUndo', () => {
  it('swaps between the before and after state', async () => {
    const { calls, writes } = recorder()
    const entry = eventEditUndo(writes, 'e', body('before'), body('after'))
    await entry.undo()
    await entry.redo()
    expect(calls).toEqual(['update e before', 'update e after'])
  })
})

describe('canUndoDelete', () => {
  const dto = (recurrence: string | null) =>
    ({
      id: 'e', calendarId: 'c', title: 'Standup', description: null, location: null, categoryId: null, color: null,
      start: '2026-11-02T08:00:00Z', end: '2026-11-02T08:15:00Z', allDay: false, recurring: !!recurrence, recurrence,
      reminders: [], attendees: [],
    }) as EventDto

  it('offers undo for a one-off', () => {
    expect(canUndoDelete(dto(null))).toBe(true)
  })

  it('does not for a series: a re-created master would lose its skipped and edited occurrences', () => {
    // A daily series with 4 Nov skipped and 5 Nov edited: the DTO is all undo would have to go on,
    // and it carries neither — the re-created series would show 4 Nov again and 5 Nov unedited.
    const series = dto('FREQ=DAILY;COUNT=5')
    const recreated = dtoToRequest(series)
    expect(Object.keys(recreated)).not.toContain('exDates')
    expect(Object.keys(recreated)).not.toContain('overrides')
    expect(canUndoDelete(series)).toBe(false)
  })
})
