import type { EventDto, SaveEventRequest } from '../api/events'

// What the editor and drag-and-drop share when they change an event: how a change is taken back
// (Ctrl+Z) and done again (Ctrl+Shift+Z). Where each gets its "before" from stays theirs — the
// editor from the state it was opened on, a drag from the grid's pre-drag box.

export interface UndoEntry {
  undo: () => Promise<unknown>
  redo: () => Promise<unknown>
}

export interface EventWrites {
  updateEvent: (id: string, body: SaveEventRequest) => Promise<unknown>
  upsertOccurrence: (seriesId: string, occurrence: string, body: SaveEventRequest) => Promise<unknown>
  resetOccurrence: (seriesId: string, occurrence: string) => Promise<unknown>
}

/** A whole event (or series) edited from `before` to `after`. */
export function eventEditUndo(writes: EventWrites, id: string, before: SaveEventRequest, after: SaveEventRequest): UndoEntry {
  return {
    undo: () => writes.updateEvent(id, before),
    redo: () => writes.updateEvent(id, after),
  }
}

/**
 * One occurrence of a series edited to `after`. Undo puts it back as it was: its own earlier edits
 * (`before`) when it had some, else plain series again (no override). Returns null when it had
 * edits but `before` isn't known — resetting would throw those edits away, so nothing is offered.
 */
export function occurrenceEditUndo(
  writes: EventWrites,
  ref: { seriesId: string; occurrence: string; hadOwnEdits: boolean },
  before: SaveEventRequest | null,
  after: SaveEventRequest,
): UndoEntry | null {
  const { seriesId, occurrence } = ref
  if (ref.hadOwnEdits && !before) return null
  return {
    undo: () =>
      ref.hadOwnEdits && before
        ? writes.upsertOccurrence(seriesId, occurrence, before)
        : writes.resetOccurrence(seriesId, occurrence),
    redo: () => writes.upsertOccurrence(seriesId, occurrence, after),
  }
}

/**
 * Whether deleting this whole event can be offered as undoable. Undo re-creates it from its DTO,
 * which carries a series' rule but not its skipped dates or edited occurrences: undoing would bring
 * skipped ones back and edited ones back to the series' times. So only a one-off is undoable; a
 * series is deleted after a confirmation instead, like "this and following".
 */
export function canUndoDelete(dto: Pick<EventDto, 'recurrence' | 'recurring'>): boolean {
  return !dto.recurring && !dto.recurrence
}

/** A server DTO → a save request, for re-creating a deleted one-off (or re-applying an occurrence's edits). */
export function dtoToRequest(dto: EventDto): SaveEventRequest {
  return {
    calendarId: dto.calendarId ?? null,
    title: dto.title,
    description: dto.description ?? null,
    location: dto.location ?? null,
    categoryId: dto.categoryId ?? null,
    start: dto.start,
    end: dto.end ?? null,
    allDay: dto.allDay,
    recurrence: dto.recurrence ?? null,
    timeZone: dto.timeZone ?? null,
    reminders: dto.reminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel })),
    attendees: dto.attendees.map((a) => ({ email: a.email, name: a.name ?? null })),
  }
}
