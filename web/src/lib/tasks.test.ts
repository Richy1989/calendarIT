import { describe, expect, it } from 'vitest'
import type { TaskDto } from '../api/tasks'
import { compareTasks, dueLabel, groupOf, groupTasks } from './tasks'

const now = new Date(2026, 11, 3, 12, 0) // Thu 3 Dec 2026, noon local

let n = 0
const task = (over: Partial<TaskDto> = {}): TaskDto => ({
  id: `t${++n}`,
  calendarId: 'c',
  title: 'Task',
  notes: null,
  due: null,
  dueAllDay: false,
  timeZone: null,
  status: 'NeedsAction',
  completedAt: null,
  priority: null,
  parentId: null,
  sortOrder: n,
  updatedAt: '2026-12-01T00:00:00Z',
  ...over,
})

describe('task groups', () => {
  it('sorts by when it is due, date-only tasks by their own day', () => {
    expect(groupOf(task({ due: '2026-12-02T00:00:00Z', dueAllDay: true }), now)).toBe('overdue')
    expect(groupOf(task({ due: '2026-12-03T00:00:00Z', dueAllDay: true }), now)).toBe('today')
    expect(groupOf(task({ due: new Date(2026, 11, 3, 9).toISOString() }), now)).toBe('overdue') // earlier today
    expect(groupOf(task({ due: new Date(2026, 11, 3, 18).toISOString() }), now)).toBe('today')
    expect(groupOf(task({ due: '2026-12-10T00:00:00Z', dueAllDay: true }), now)).toBe('upcoming')
    expect(groupOf(task(), now)).toBe('someday')
    expect(groupOf(task({ status: 'Completed', due: '2026-12-01T00:00:00Z', dueAllDay: true }), now)).toBe('done')
  })

  it('puts subtasks under their task, and orphans on their own', () => {
    const parent = task({ title: 'Move' })
    const sub = task({ title: 'Pack', parentId: parent.id })
    const orphan = task({ title: 'Lonely', parentId: 'gone' })
    const groups = groupTasks([sub, parent, orphan], now)
    expect(groups).toHaveLength(1)
    expect(groups[0].nodes.map((x) => x.task.title)).toEqual(['Move', 'Lonely'])
    expect(groups[0].nodes[0].subtasks.map((s) => s.title)).toEqual(['Pack'])
  })

  it('orders by due date, then priority', () => {
    const later = task({ due: '2026-12-09T00:00:00Z', dueAllDay: true })
    const sooner = task({ due: '2026-12-05T00:00:00Z', dueAllDay: true, priority: 'low' })
    const soonerHigh = task({ due: '2026-12-05T00:00:00Z', dueAllDay: true, priority: 'high' })
    expect([later, sooner, soonerHigh].sort(compareTasks)).toEqual([soonerHigh, sooner, later])
  })
})

describe('dueLabel', () => {
  it('names near days and keeps the time of a timed task', () => {
    expect(dueLabel(task({ due: '2026-12-03T00:00:00Z', dueAllDay: true }), false, now)).toBe('Today')
    expect(dueLabel(task({ due: '2026-12-04T00:00:00Z', dueAllDay: true }), false, now)).toBe('Tomorrow')
    expect(dueLabel(task({ due: new Date(2026, 11, 3, 18, 30).toISOString() }), false, now)).toMatch(/^Today, 18:30$/)
    expect(dueLabel(task(), false, now)).toBeNull()
  })
})
