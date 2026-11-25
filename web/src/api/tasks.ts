import { api } from './client'
import type { components } from './schema'
import { problemMessage } from './errors'

export type TaskDto = components['schemas']['TaskDto']
export type SaveTaskRequest = components['schemas']['SaveTaskRequest']
export type TaskPriority = 'high' | 'normal' | 'low'

/** Open tasks; with `includeDone`, finished ones too. */
export async function listTasks(includeDone = false): Promise<TaskDto[]> {
  const { data, error } = await api.GET('/api/tasks', { params: { query: { includeDone } } })
  if (error || !data) throw new Error('Failed to load tasks')
  return data
}

export async function createTask(body: SaveTaskRequest): Promise<TaskDto> {
  const { data, error } = await api.POST('/api/tasks', { body })
  if (error || !data) throw new Error(problemMessage(error, 'Couldn’t add the task'))
  return data
}

export async function updateTask(id: string, body: SaveTaskRequest): Promise<TaskDto> {
  const { data, error } = await api.PUT('/api/tasks/{id}', { params: { path: { id } }, body })
  if (error || !data) throw new Error(problemMessage(error, 'Couldn’t save the task'))
  return data
}

/** Ticks a task off (with its open subtasks), or opens it again. */
export async function setTaskDone(id: string, done: boolean): Promise<TaskDto> {
  const { data, error } = await api.PUT('/api/tasks/{id}/done', { params: { path: { id } }, body: { done } })
  if (error || !data) throw new Error(problemMessage(error, 'Couldn’t update the task'))
  return data
}

/** Deletes a task and its subtasks. */
export async function deleteTask(id: string): Promise<void> {
  const { error } = await api.DELETE('/api/tasks/{id}', { params: { path: { id } } })
  if (error) throw new Error('Couldn’t delete the task')
}

/** A task as a save request — for edits that change one thing (a new due date, say). */
export function taskToRequest(t: TaskDto): SaveTaskRequest {
  return {
    calendarId: t.calendarId,
    title: t.title,
    notes: t.notes,
    due: t.due,
    dueAllDay: t.dueAllDay,
    timeZone: t.timeZone,
    priority: t.priority,
    parentId: t.parentId,
    sortOrder: Number(t.sortOrder),
  }
}
