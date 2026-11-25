// Every 30 days the app suggests a fresh .ics export, so a self-hosted calendar is never only
// in one place. Per browser and account, in localStorage: nothing for the server to track.

const DAY = 24 * 60 * 60 * 1000
export const BACKUP_EVERY_MS = 30 * DAY
/** "Later" asks again in a week. */
export const SNOOZE_MS = 7 * DAY

const keyFor = (userId: string) => `calendarit.backup.${userId}`

/** When to ask next, given what was stored (an epoch-ms string, or nothing yet). A first visit
 *  starts the clock rather than asking at once: a new account has nothing to back up. */
export function nextReminderAt(stored: string | null, now: number): { due: number; store?: string } {
  const at = stored === null ? NaN : Number(stored)
  if (!Number.isFinite(at)) return { due: now + BACKUP_EVERY_MS, store: String(now + BACKUP_EVERY_MS) }
  return { due: at }
}

export function isBackupDue(userId: string, now = Date.now()): boolean {
  try {
    const { due, store } = nextReminderAt(localStorage.getItem(keyFor(userId)), now)
    if (store) localStorage.setItem(keyFor(userId), store)
    return now >= due
  } catch {
    return false // storage blocked: never nag
  }
}

/** After an export: the next reminder is 30 days out. */
export function markBackedUp(userId: string, now = Date.now()): void {
  try {
    localStorage.setItem(keyFor(userId), String(now + BACKUP_EVERY_MS))
  } catch {
    /* storage blocked */
  }
}

export function snoozeBackupReminder(userId: string, now = Date.now()): void {
  try {
    localStorage.setItem(keyFor(userId), String(now + SNOOZE_MS))
  } catch {
    /* storage blocked */
  }
}
