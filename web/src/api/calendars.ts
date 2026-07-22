import { api } from './client'
import type { components } from './schema'

export type CalendarDto = components['schemas']['CalendarDto']

export async function listCalendars(): Promise<CalendarDto[]> {
  const { data, error } = await api.GET('/api/calendars')
  if (error || !data) throw new Error('Failed to load calendars')
  return data
}

export async function createCalendar(name: string): Promise<CalendarDto> {
  const { data, error } = await api.POST('/api/calendars', { body: { name } })
  if (error || !data) throw new Error('Failed to create calendar')
  return data
}

export async function renameCalendar(id: string, name: string): Promise<CalendarDto> {
  const { data, error } = await api.PUT('/api/calendars/{id}', { params: { path: { id } }, body: { name } })
  if (error || !data) throw new Error('Failed to rename calendar')
  return data
}

/** Deletes a calendar and all its events. The server refuses to delete the last one (409). */
export async function deleteCalendar(id: string): Promise<void> {
  const { error, response } = await api.DELETE('/api/calendars/{id}', { params: { path: { id } } })
  if (error) {
    throw new Error(response?.status === 409 ? "You can't delete your last calendar." : 'Failed to delete calendar')
  }
}
