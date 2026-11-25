// How the task panel orders and groups tasks, and how a due date reads. Kept free of React so
// it can be tested directly.

import type { TaskDto } from '../api/tasks'
import { allDayDate, dayKey, formatDateMedium, formatTime } from './dates'

export type TaskGroupId = 'overdue' | 'today' | 'upcoming' | 'someday' | 'done'

export const GROUP_LABELS: Record<TaskGroupId, string> = {
  overdue: 'Overdue',
  today: 'Today',
  upcoming: 'Upcoming',
  someday: 'No date',
  done: 'Done',
}

export const isDone = (t: TaskDto) => t.status === 'Completed' || t.status === 'Cancelled'

/** The moment a task is due, or null. A date-only due is that local day. */
export function dueDate(t: TaskDto): Date | null {
  if (!t.due) return null
  return t.dueAllDay ? allDayDate(t.due) : new Date(t.due)
}

/** Which group a top-level task falls in. A date-only task is overdue from the day after. */
export function groupOf(t: TaskDto, now: Date = new Date()): TaskGroupId {
  if (isDone(t)) return 'done'
  const due = dueDate(t)
  if (!due) return 'someday'
  const today = dayKey(now)
  const day = dayKey(due)
  if (day < today || (!t.dueAllDay && due.getTime() < now.getTime())) return 'overdue'
  return day === today ? 'today' : 'upcoming'
}

const PRIORITY_RANK: Record<string, number> = { high: 0, normal: 1, low: 2 }

/** Earliest due first, then higher priority, then the list order. */
export function compareTasks(a: TaskDto, b: TaskDto): number {
  const da = dueDate(a)?.getTime() ?? Infinity
  const db = dueDate(b)?.getTime() ?? Infinity
  if (da !== db) return da - db
  const pa = PRIORITY_RANK[a.priority ?? ''] ?? 1.5
  const pb = PRIORITY_RANK[b.priority ?? ''] ?? 1.5
  if (pa !== pb) return pa - pb
  return Number(a.sortOrder) - Number(b.sortOrder)
}

export type TaskNode = { task: TaskDto; subtasks: TaskDto[] }

/**
 * Top-level tasks by group, each with its subtasks. A subtask whose parent isn't in the list (a
 * finished parent while only open tasks are loaded) shows on its own.
 */
export function groupTasks(tasks: TaskDto[], now: Date = new Date()): { id: TaskGroupId; nodes: TaskNode[] }[] {
  const ids = new Set(tasks.map((t) => t.id))
  const children = new Map<string, TaskDto[]>()
  const roots: TaskDto[] = []
  for (const t of tasks) {
    if (t.parentId && ids.has(t.parentId)) {
      children.set(t.parentId, [...(children.get(t.parentId) ?? []), t])
    } else {
      roots.push(t)
    }
  }
  const order: TaskGroupId[] = ['overdue', 'today', 'upcoming', 'someday', 'done']
  return order
    .map((id) => ({
      id,
      nodes: roots
        .filter((t) => groupOf(t, now) === id)
        .sort(id === 'done' ? (a, b) => (b.completedAt ?? '').localeCompare(a.completedAt ?? '') : compareTasks)
        .map((task) => ({
          task,
          subtasks: (children.get(task.id) ?? []).sort((a, b) => Number(a.sortOrder) - Number(b.sortOrder)),
        })),
    }))
    .filter((g) => g.nodes.length > 0)
}

/** "Today", "Tomorrow", "Fri, Dec 4", plus the time for a timed due. */
export function dueLabel(t: TaskDto, hour12: boolean, now: Date = new Date()): string | null {
  const due = dueDate(t)
  if (!due) return null
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const days = Math.round((new Date(due.getFullYear(), due.getMonth(), due.getDate()).getTime() - today.getTime()) / 86_400_000)
  const day =
    days === 0
      ? 'Today'
      : days === 1
        ? 'Tomorrow'
        : days === -1
          ? 'Yesterday'
          : Math.abs(days) < 7
            ? due.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })
            : formatDateMedium(due)
  return t.dueAllDay ? day : `${day}, ${formatTime(due, hour12)}`
}

/** The API's due for an editor value: a date at midnight UTC, or the local time as an instant. */
export function dueToApi(value: string, allDay: boolean): string {
  return allDay ? `${value.slice(0, 10)}T00:00:00Z` : new Date(value).toISOString()
}
