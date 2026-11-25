import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BACKUP_EVERY_MS, SNOOZE_MS, isBackupDue, markBackedUp, nextReminderAt, snoozeBackupReminder } from './backupReminder'

describe('backup reminder', () => {
  beforeEach(() => {
    // Tests run in Node: a Map stands in for the browser's storage.
    const store = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => void store.set(k, v),
      clear: () => store.clear(),
    })
  })

  it('starts the clock on a first visit instead of asking at once', () => {
    expect(nextReminderAt(null, 1000)).toEqual({ due: 1000 + BACKUP_EVERY_MS, store: String(1000 + BACKUP_EVERY_MS) })
    expect(isBackupDue('u', 1000)).toBe(false)
    expect(isBackupDue('u', 1000 + BACKUP_EVERY_MS)).toBe(true)
  })

  it('an export resets it to 30 days; "later" to a week', () => {
    markBackedUp('u', 0)
    expect(isBackupDue('u', BACKUP_EVERY_MS - 1)).toBe(false)
    snoozeBackupReminder('u', BACKUP_EVERY_MS)
    expect(isBackupDue('u', BACKUP_EVERY_MS + SNOOZE_MS - 1)).toBe(false)
    expect(isBackupDue('u', BACKUP_EVERY_MS + SNOOZE_MS)).toBe(true)
  })

  it('treats a garbled value like a first visit, and keeps accounts apart', () => {
    localStorage.setItem('calendarit.backup.u', 'junk')
    expect(isBackupDue('u', 5)).toBe(false)
    markBackedUp('a', 0)
    expect(isBackupDue('b', BACKUP_EVERY_MS)).toBe(false) // b's clock only starts now
  })
})
