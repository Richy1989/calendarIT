import { describe, expect, it } from 'vitest'
import { buildRule, defaultSpec, describeRule, parseRule, type RuleSpec } from './rrule'

// Tue 13 Oct 2026, 09:00 local — the second Tuesday of the month.
const start = new Date(2026, 9, 13, 9, 0, 0)

describe('buildRule', () => {
  it('writes the presets back exactly as they were', () => {
    // Opening a preset rule in the editor and saving must not change its text.
    for (const rule of ['FREQ=DAILY', 'FREQ=WEEKLY', 'FREQ=MONTHLY', 'FREQ=YEARLY', 'FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR']) {
      const spec = parseRule(rule, start)!
      expect(buildRule(spec, start, false)).toBe(rule)
    }
  })

  it('every 4 weeks on two days', () => {
    const spec: RuleSpec = { ...defaultSpec(start), interval: 4, byDay: ['TU', 'MO'] }
    expect(buildRule(spec, start, false)).toBe('FREQ=WEEKLY;INTERVAL=4;BYDAY=MO,TU')
  })

  it('every 3 days, 10 times', () => {
    const spec: RuleSpec = { ...defaultSpec(start, 'DAILY'), interval: 3, end: { kind: 'count', count: 10 } }
    expect(buildRule(spec, start, false)).toBe('FREQ=DAILY;INTERVAL=3;COUNT=10')
  })

  it('monthly on the start weekday of the month', () => {
    const spec: RuleSpec = { ...defaultSpec(start, 'MONTHLY'), monthly: 'weekday' }
    expect(buildRule(spec, start, false)).toBe('FREQ=MONTHLY;BYDAY=2TU')
  })

  it('a fifth weekday is written as the last one', () => {
    const lastFriday = new Date(2026, 9, 30, 9, 0, 0)
    const spec: RuleSpec = { ...defaultSpec(lastFriday, 'MONTHLY'), monthly: 'weekday' }
    expect(buildRule(spec, lastFriday, false)).toBe('FREQ=MONTHLY;BYDAY=-1FR')
  })

  it('UNTIL is a DATE for all-day series and an end-of-day UTC instant otherwise', () => {
    const spec: RuleSpec = { ...defaultSpec(start), end: { kind: 'until', date: '2026-12-31' } }
    expect(buildRule(spec, start, true)).toBe('FREQ=WEEKLY;UNTIL=20261231')
    const timed = buildRule(spec, start, false)
    expect(timed).toMatch(/^FREQ=WEEKLY;UNTIL=\d{8}T\d{6}Z$/)
    // ...and it reads back as the same local day.
    expect(parseRule(timed, start)!.end).toEqual({ kind: 'until', date: '2026-12-31' })
  })

  it('clamps nonsense numbers', () => {
    const spec: RuleSpec = { ...defaultSpec(start, 'DAILY'), interval: 0, end: { kind: 'count', count: 5000 } }
    expect(buildRule(spec, start, false)).toBe('FREQ=DAILY;COUNT=999')
  })
})

describe('parseRule', () => {
  it('round-trips what it can represent', () => {
    for (const rule of ['FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE', 'FREQ=MONTHLY;BYDAY=2TU;COUNT=3', 'FREQ=YEARLY;INTERVAL=2']) {
      expect(buildRule(parseRule(rule, start)!, start, false)).toBe(rule)
    }
  })

  it('accepts an RRULE: prefix and lower case', () => {
    expect(parseRule('rrule:freq=daily;interval=2', start)?.interval).toBe(2)
  })

  it('leaves rules it cannot represent alone', () => {
    for (const rule of [
      'FREQ=MONTHLY;BYDAY=TU;BYSETPOS=2', // BYSETPOS
      'FREQ=MONTHLY;BYMONTHDAY=1,15', // several days
      'FREQ=MONTHLY;BYDAY=3WE', // not the start's weekday-of-month
      'FREQ=HOURLY', // not offered
      'FREQ=WEEKLY;COUNT=2;UNTIL=20261231', // both ends
      'garbage',
    ]) {
      expect(parseRule(rule, start), rule).toBeNull()
    }
  })

  it('tolerates apps that spell out the start day', () => {
    expect(parseRule('FREQ=MONTHLY;BYMONTHDAY=13', start)?.monthly).toBe('day')
    expect(parseRule('FREQ=YEARLY;BYMONTH=10', start)?.freq).toBe('YEARLY')
  })
})

describe('describeRule', () => {
  it('reads like a sentence', () => {
    expect(describeRule('FREQ=DAILY', start)).toBe('Every day')
    expect(describeRule('FREQ=DAILY;INTERVAL=3;COUNT=10', start)).toBe('Every 3 days · 10 times')
    expect(describeRule('FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR', start)).toBe('Every week on weekdays')
    expect(describeRule('FREQ=MONTHLY;BYDAY=2TU', start)).toContain('on the second')
    expect(describeRule('FREQ=MONTHLY', start)).toBe('Every month on day 13')
    expect(describeRule('FREQ=MONTHLY;BYDAY=TU;BYSETPOS=2', start)).toBe('Custom rule')
  })
})
