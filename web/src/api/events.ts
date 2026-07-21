import { api } from './client'
import type { components } from './schema'

export type EventDto = components['schemas']['EventDto']
export type SaveEventRequest = components['schemas']['SaveEventRequest']

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
