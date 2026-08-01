import { describe, expect, it } from 'vitest'
import { RETENTION_MS, selectToShow, type ShownEntry } from './localReminderStore'

const item = (reminderId: string, occ: string) => ({
  reminderId, occurrenceStartUtc: occ, title: 'Dentist', location: null,
})

describe('selectToShow', () => {
  it('shows a brand-new item and records its key', () => {
    const { toShow, nextShown } = selectToShow([], [item('r1', '2026-09-01T08:00:00Z')], 1000)
    expect(toShow).toHaveLength(1)
    expect(nextShown.map((e) => e.key)).toContain('r1:2026-09-01T08:00:00Z')
  })

  it('suppresses an item already shown', () => {
    const shown: ShownEntry[] = [{ key: 'r1:2026-09-01T08:00:00Z', at: 900 }]
    const { toShow } = selectToShow(shown, [item('r1', '2026-09-01T08:00:00Z')], 1000)
    expect(toShow).toHaveLength(0)
  })

  it('does not show the same item twice within one batch', () => {
    const dup = item('r1', '2026-09-01T08:00:00Z')
    const { toShow } = selectToShow([], [dup, dup], 1000)
    expect(toShow).toHaveLength(1)
  })

  it('prunes keys older than the retention window', () => {
    const old: ShownEntry[] = [{ key: 'old:x', at: 0 }]
    const { nextShown } = selectToShow(old, [], RETENTION_MS + 1)
    expect(nextShown.find((e) => e.key === 'old:x')).toBeUndefined()
  })
})
