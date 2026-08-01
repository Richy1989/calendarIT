// web/src/push/localReminders.ts
/* Local-mode fallback: while the app is open and this browser is in 'local' notify mode, poll the
 * backend for due browser-channel reminders and show them via the service worker. No push service
 * (no Google) is involved. Dedup lives in localReminderStore; this module owns timing + display. */

import { getDueReminders } from '../api/reminders'
import { getNotifyMode } from './webPush'
import { RETENTION_MS, selectToShow, type ShownEntry } from './localReminderStore'

const POLL_MS = 30_000
const SHOWN_STORAGE_KEY = 'calendarit.shownReminders'

let timer: number | null = null
let sinceUtc = ''

function loadShown(now: number): ShownEntry[] {
  try {
    const raw = JSON.parse(localStorage.getItem(SHOWN_STORAGE_KEY) ?? '[]') as ShownEntry[]
    return raw.filter((e) => e && typeof e.key === 'string' && now - e.at < RETENTION_MS)
  } catch {
    return []
  }
}

function saveShown(entries: ShownEntry[]): void {
  localStorage.setItem(SHOWN_STORAGE_KEY, JSON.stringify(entries))
}

function bodyFor(occurrenceStartUtc: string, location?: string | null): string {
  const when = new Date(occurrenceStartUtc).toLocaleString()
  return location ? `Starts at ${when}.\nLocation: ${location}` : `Starts at ${when}.`
}

async function pollOnce(): Promise<void> {
  if (getNotifyMode() !== 'local') return
  const now = Date.now()
  const items = await getDueReminders(sinceUtc)
  if (items === null) return // fetch failed — keep sinceUtc so this window is retried next tick
  sinceUtc = new Date(now).toISOString()

  const { toShow, nextShown } = selectToShow(loadShown(now), items, now)
  saveShown(nextShown)
  if (toShow.length === 0) return

  const reg = await navigator.serviceWorker.ready
  for (const item of toShow) {
    void reg
      .showNotification(`Reminder: ${item.title}`, {
        body: bodyFor(item.occurrenceStartUtc, item.location),
        tag: `${item.reminderId}:${item.occurrenceStartUtc}`,
        data: { url: '/' },
      })
      .catch(() => {})
  }
}

const onWake = () => { void pollOnce().catch(() => {}) }

/** Begin polling (idempotent). Safe to call on login and after enabling local mode. */
export function startLocalReminderPoller(): void {
  if (timer !== null) return
  sinceUtc = new Date().toISOString()
  timer = window.setInterval(onWake, POLL_MS)
  window.addEventListener('focus', onWake)
  void pollOnce().catch(() => {})
}

/** Stop polling and detach listeners (idempotent). */
export function stopLocalReminderPoller(): void {
  if (timer !== null) {
    window.clearInterval(timer)
    timer = null
  }
  window.removeEventListener('focus', onWake)
}
