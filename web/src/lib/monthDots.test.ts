import { describe, expect, it } from 'vitest'
import type { EventDto } from '../api/events'
import { dotsByDay } from './monthDots'

const ev = (over: Partial<EventDto>): EventDto =>
  ({ id: 'e', calendarId: 'c', title: 't', start: '', end: null, allDay: false, color: '#111111', ...over }) as EventDto

describe('dotsByDay', () => {
  it('marks every day an appointment covers, in its color', () => {
    const dots = dotsByDay([
      ev({ start: '2026-12-03T00:00:00Z', end: '2026-12-05T00:00:00Z', allDay: true, color: '#aa0000' }),
      ev({ start: new Date(2026, 11, 4, 10).toISOString(), end: new Date(2026, 11, 4, 11).toISOString(), color: '#00aa00' }),
    ])
    expect(dots.get('2026-12-03')).toEqual(['#aa0000'])
    expect(dots.get('2026-12-04')).toEqual(['#aa0000', '#00aa00'])
    expect(dots.get('2026-12-05')).toEqual(['#aa0000'])
    expect(dots.get('2026-12-06')).toBeUndefined()
  })

  it('keeps each color once, at most three, and doesn’t spill past a midnight end', () => {
    const at = (h: number, color: string, endH = h + 1) =>
      ev({ start: new Date(2026, 11, 7, h).toISOString(), end: new Date(2026, 11, 7, endH).toISOString(), color })
    const dots = dotsByDay([at(8, '#1'), at(9, '#1'), at(10, '#2'), at(11, '#3'), at(12, '#4'), at(22, '#5', 24)])
    expect(dots.get('2026-12-07')).toEqual(['#1', '#2', '#3'])
    expect(dots.get('2026-12-08')).toBeUndefined()
  })
})
