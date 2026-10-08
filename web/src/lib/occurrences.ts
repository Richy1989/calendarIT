import type { EventDto } from '../api/events'

/**
 * One occurrence of a repeating series, as the occurrence endpoints address it: the series, and
 * the start the occurrence has in the series' rule. `overrideId` is set once the occurrence has
 * edits of its own; `start`/`end` are its current (possibly moved) times, API-shaped.
 */
export type OccurrenceRef = {
  seriesId: string
  occurrence: string
  overrideId: string | null
  start: string
  end: string | null
  allDay: boolean
}

/** The occurrence a DTO stands for, or null for a one-off event. */
export function occurrenceOf(dto: EventDto): OccurrenceRef | null {
  if (!dto.recurring || !dto.recurrenceId) return null
  return {
    seriesId: dto.seriesMasterId ?? dto.id,
    occurrence: dto.recurrenceId,
    overrideId: dto.seriesMasterId ? dto.id : null,
    start: dto.start,
    end: dto.end ?? null,
    allDay: dto.allDay,
  }
}
