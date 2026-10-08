import { api } from './client'
import type { components } from './schema'
import { authHeaders } from '../auth/session'
import { problemMessage } from './errors'

export type EventDto = components['schemas']['EventDto']
export type SaveEventRequest = components['schemas']['SaveEventRequest']
export type ImportResult = components['schemas']['ImportResult']

/** Downloads the user's events as an .ics blob — everything, or only the given calendars. */
export async function exportIcs(calendarIds?: string[]): Promise<Blob> {
  const query = calendarIds?.length ? `?calendars=${calendarIds.map(encodeURIComponent).join(',')}` : ''
  const res = await fetch(`/api/events/export.ics${query}`, { headers: await authHeaders() })
  if (!res.ok) throw new Error('Export failed')
  return res.blob()
}

/** Fetches a single event serialized as a standards-compliant .ics VCALENDAR — used to put
 *  iCalendar text on the *system* clipboard so other calendar apps can paste it. */
export async function exportEventIcs(id: string): Promise<string> {
  const res = await fetch(`/api/events/${encodeURIComponent(id)}/export.ics`, { headers: await authHeaders() })
  if (!res.ok) throw new Error('Export failed')
  return res.text()
}

/** Where an import lands: an existing calendar, or a brand-new one with this name (and, optionally,
 *  a default category for its events). */
export type ImportTarget = { calendarId?: string; newCalendarName?: string; newCalendarCategoryId?: string | null }

/** Uploads an .ics document; returns how many events were imported / skipped. */
export async function importIcs(ics: string, target?: ImportTarget): Promise<ImportResult> {
  const params = new URLSearchParams()
  if (target?.newCalendarName) {
    params.set('newCalendarName', target.newCalendarName)
    if (target.newCalendarCategoryId) params.set('newCalendarCategoryId', target.newCalendarCategoryId)
  } else if (target?.calendarId) {
    params.set('calendarId', target.calendarId)
  }
  const qs = params.toString()
  const res = await fetch(`/api/events/import${qs ? `?${qs}` : ''}`, {
    method: 'POST',
    headers: { ...(await authHeaders()), 'Content-Type': 'text/calendar' },
    body: ics,
  })
  if (!res.ok) throw new Error(problemMessage(await res.json().catch(() => null), 'Import failed'))
  return res.json()
}

/** Events overlapping [from, to) — recurring series expanded into occurrences. */
export async function listEvents(from: string, to: string): Promise<EventDto[]> {
  const { data, error } = await api.GET('/api/events', { params: { query: { from, to } } })
  if (error || !data) throw new Error('Failed to load events')
  return data
}

/** A lightweight search hit. `start` is the next occurrence for a recurring series. */
export type EventSearchResult = {
  id: string
  title: string
  location: string | null
  color: string | null
  start: string
  allDay: boolean
  recurring: boolean
}

/** Searches the user's events by title/location; recurring series appear once. */
export async function searchEvents(q: string, limit = 8): Promise<EventSearchResult[]> {
  const params = new URLSearchParams({ q, limit: String(limit) })
  const res = await fetch(`/api/events/search?${params.toString()}`, { headers: await authHeaders() })
  if (!res.ok) throw new Error('Search failed')
  return res.json()
}

export async function getEvent(id: string): Promise<EventDto> {
  const { data, error } = await api.GET('/api/events/{id}', { params: { path: { id } } })
  if (error || !data) throw new Error('Failed to load event')
  return data
}

export async function createEvent(body: SaveEventRequest): Promise<EventDto> {
  const { data, error } = await api.POST('/api/events', { body })
  if (error || !data) throw new Error(problemMessage(error, 'Failed to create event'))
  return data
}

export async function updateEvent(id: string, body: SaveEventRequest): Promise<EventDto> {
  const { data, error } = await api.PUT('/api/events/{id}', { params: { path: { id } }, body })
  if (error || !data) throw new Error(problemMessage(error, 'Failed to update event'))
  return data
}

/** Edits one occurrence of a series — the one that starts at `occurrence` in the series' rule. */
export async function updateOccurrence(seriesId: string, occurrence: string, body: SaveEventRequest): Promise<EventDto> {
  const { data, error } = await api.PUT('/api/events/{id}/occurrence', {
    params: { path: { id: seriesId }, query: { occurrence } },
    body,
  })
  if (error || !data) throw new Error(problemMessage(error, 'Failed to update this occurrence'))
  return data
}

/** Puts one occurrence back to what the series says (undoes an edit or a delete of it). */
export async function resetOccurrence(seriesId: string, occurrence: string): Promise<void> {
  const { error } = await api.POST('/api/events/{id}/occurrence/reset', {
    params: { path: { id: seriesId }, query: { occurrence } },
  })
  if (error) throw new Error('Failed to restore this occurrence')
}

/** Our RSVP to a received invitation. */
export type RsvpStatus = 'Accepted' | 'Declined' | 'Tentative'

/** Records the user's RSVP to a received invitation and mails a REPLY back to the organizer. */
export async function respondToInvitation(id: string, status: RsvpStatus): Promise<EventDto> {
  const res = await fetch(`/api/events/${encodeURIComponent(id)}/rsvp`, {
    method: 'POST',
    headers: { ...(await authHeaders()), 'Content-Type': 'application/json' },
    body: JSON.stringify({ status }),
  })
  if (!res.ok) throw new Error('RSVP failed')
  return res.json()
}

/** Deletes the whole event, or — for a recurring series — just one occurrence when `occurrence` is given. */
export async function deleteEvent(id: string, occurrence?: string): Promise<void> {
  const { error } = await api.DELETE('/api/events/{id}', {
    params: { path: { id }, query: occurrence ? { occurrence } : {} },
  })
  if (error) throw new Error('Failed to delete event')
}
