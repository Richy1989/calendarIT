import { api } from './client'
import type { components } from './schema'
import { problemMessage } from './errors'

export type CalendarDto = components['schemas']['CalendarDto']

export async function listCalendars(): Promise<CalendarDto[]> {
  const { data, error } = await api.GET('/api/calendars')
  if (error || !data) throw new Error('Failed to load calendars')
  return data
}

/** Creates a calendar, optionally with a default category for events that have none. */
export async function createCalendar(name: string, defaultCategoryId?: string | null): Promise<CalendarDto> {
  const { data, error } = await api.POST('/api/calendars', { body: { name, defaultCategoryId: defaultCategoryId ?? null } })
  if (error || !data) throw new Error(problemMessage(error, 'Failed to create calendar'))
  return data
}

/** Sets (or, with null, clears) the category this calendar's events take when they have none. */
export async function setCalendarCategory(id: string, categoryId: string | null): Promise<CalendarDto> {
  const { data, error } = await api.PUT('/api/calendars/{id}/default-category', {
    params: { path: { id } },
    body: { categoryId },
  })
  if (error || !data) throw new Error(problemMessage(error, 'Failed to set the calendar’s category'))
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
