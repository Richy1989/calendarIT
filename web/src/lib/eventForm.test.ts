import { describe, expect, it } from 'vitest'
import { defaultRemindersFor, describeReminder, repeatPresets, REPEAT_RULES, sameReminders } from './eventForm'

describe('repeatPresets', () => {
  it('names the start’s own day, and keeps the rules the full editor knows', () => {
    const presets = repeatPresets(new Date(2026, 6, 15, 10)) // Wednesday 15 July 2026
    expect(presets.map((p) => p.rule)).toEqual(Object.values(REPEAT_RULES))
    const weekly = presets.find((p) => p.preset === 'weekly')!
    expect(weekly.label).toContain(new Date(2026, 6, 15).toLocaleDateString(undefined, { weekday: 'long' }))
    expect(presets.find((p) => p.preset === 'monthly')!.label).toBe('Monthly on day 15')
  })
})

describe('defaultRemindersFor', () => {
  const calendar = { defaultReminders: [{ minutesBefore: '15', channel: 'WebPush' }, { minutesBefore: 1440, channel: 'Email' }] }

  it('gives a timed event the calendar’s defaults, as numbers', () => {
    expect(defaultRemindersFor(calendar, false)).toEqual([
      { minutesBefore: 15, channel: 'WebPush' },
      { minutesBefore: 1440, channel: 'Email' },
    ])
  })

  it('gives an all-day event none, and copes with a calendar without defaults', () => {
    expect(defaultRemindersFor(calendar, true)).toEqual([])
    expect(defaultRemindersFor({ defaultReminders: null }, false)).toEqual([])
    expect(defaultRemindersFor(undefined, false)).toEqual([])
  })
})

describe('reminder helpers', () => {
  it('describes a reminder briefly', () => {
    expect(describeReminder({ minutesBefore: 15, channel: 'WebPush' })).toBe('15 min before · Notification')
    expect(describeReminder({ minutesBefore: 7, channel: 'Email' })).toBe('7 min before · Email')
  })

  it('compares lists in order', () => {
    const a = [{ minutesBefore: 15, channel: 'Email' }]
    expect(sameReminders(a, [{ minutesBefore: 15, channel: 'Email' }])).toBe(true)
    expect(sameReminders(a, [{ minutesBefore: 15, channel: 'WebPush' }])).toBe(false)
    expect(sameReminders(a, [])).toBe(false)
  })
})
