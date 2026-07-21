import { api } from './client'
import type { components } from './schema'
import { getAccessToken } from '../auth/authStorage'

export type EventDto = components['schemas']['EventDto']
export type SaveEventRequest = components['schemas']['SaveEventRequest']
export type ImportResult = components['schemas']['ImportResult']

function authHeaders(): Record<string, string> {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** Downloads the user's whole calendar as an .ics blob. */
export async function exportIcs(): Promise<Blob> {
  const res = await fetch('/api/events/export.ics', { headers: authHeaders() })
  if (!res.ok) throw new Error('Export failed')
  return res.blob()
}

/** Uploads an .ics document; returns how many events were imported / skipped. */
export async function importIcs(ics: string): Promise<ImportResult> {
  const res = await fetch('/api/events/import', {
    method: 'POST',
    headers: { ...authHeaders(), 'Content-Type': 'text/calendar' },
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
