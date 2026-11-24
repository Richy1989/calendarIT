import { describe, expect, it } from 'vitest'
import { allDayDate, toOffsetIso } from './dates'

describe('toOffsetIso', () => {
  it('writes the local clock with its offset, naming the same instant', () => {
    // Either side of a DST change (wherever the tests run), where the offset differs.
    for (const d of [new Date(2026, 0, 15, 0, 0, 0), new Date(2026, 6, 15, 23, 59, 58)]) {
      const iso = toOffsetIso(d)
      expect(iso).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/)
      expect(new Date(iso).getTime()).toBe(d.getTime())
      expect(iso.slice(11, 19)).toBe(d.toTimeString().slice(0, 8)) // the local wall clock, not UTC
    }
  })
})

describe('allDayDate', () => {
  it('reads an all-day start as that date here, whatever the zone', () => {
    const d = allDayDate('2026-10-12T00:00:00+00:00')
    expect([d.getFullYear(), d.getMonth(), d.getDate(), d.getHours()]).toEqual([2026, 9, 12, 0])
  })
})
