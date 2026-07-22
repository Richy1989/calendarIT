import { api } from './client'
import type { components } from './schema'
import { ensureAccessToken } from '../auth/session'

export type EventDto = components['schemas']['EventDto']
export type SaveEventRequest = components['schemas']['SaveEventRequest']
export type ImportResult = components['schemas']['ImportResult']

async function authHeaders(): Promise<Record<string, string>> {
  const token = await ensureAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** Downloads the user's whole calendar as an .ics blob. */
export async function exportIcs(): Promise<Blob> {
  const res = await fetch('/api/events/export.ics', { headers: await authHeaders() })
  if (!res.ok) throw new Error('Export failed')
  return res.blob()
}

/** Uploads an .ics document; returns how many events were imported / skipped. */
export async function importIcs(ics: string): Promise<ImportResult> {
  const res = await fetch('/api/events/import', {
    method: 'POST',
    headers: { ...(await authHeaders()), 'Content-Type': 'text/calendar' },
    body: ics,
  })
  if (!res.ok) throw new Error('Import failed')
  return res.json()
}

export async function listEvents(from?: string, to?: string): Promise<EventDto[]> {
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
  if (error || !data) throw new Error('Failed to create event')
  return data
}

export async function updateEvent(id: string, body: SaveEventRequest): Promise<EventDto> {
  const { data, error } = await api.PUT('/api/events/{id}', { params: { path: { id } }, body })
  if (error || !data) throw new Error('Failed to update event')
  return data
}

/** Deletes the whole event, or — for a recurring series — just one occurrence when `occurrence` is given. */
export async function deleteEvent(id: string, occurrence?: string): Promise<void> {
  const { error } = await api.DELETE('/api/events/{id}', {
    params: { path: { id }, query: occurrence ? { occurrence } : {} },
  })
  if (error) throw new Error('Failed to delete event')
}
