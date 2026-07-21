import { api } from './client'
import type { components } from './schema'

export type EventDto = components['schemas']['EventDto']
export type SaveEventRequest = components['schemas']['SaveEventRequest']

export async function listEvents(): Promise<EventDto[]> {
  const { data, error } = await api.GET('/api/events', {})
  if (error || !data) throw new Error('Failed to load events')
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

export async function deleteEvent(id: string): Promise<void> {
  const { error } = await api.DELETE('/api/events/{id}', { params: { path: { id } } })
  if (error) throw new Error('Failed to delete event')
}
