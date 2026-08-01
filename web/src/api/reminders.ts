// web/src/api/reminders.ts
import { api } from './client'
import type { DueItem } from '../push/localReminderStore'

/** Due browser-channel reminders since the given ISO timestamp (server clamps to the last hour).
 * Returns `null` on request failure so callers can distinguish "nothing due" from "unknown". */
export async function getDueReminders(sinceUtc: string): Promise<DueItem[] | null> {
  const { data, error } = await api.GET('/api/reminders/due', {
    params: { query: { sinceUtc } },
  })
  if (error || !data) return null
  return (data.items ?? []).map((i) => ({
    reminderId: String(i.reminderId),
    occurrenceStartUtc: String(i.occurrenceStartUtc),
    title: i.title,
    location: i.location ?? null,
  }))
}
