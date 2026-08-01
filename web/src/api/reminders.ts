// web/src/api/reminders.ts
import { api } from './client'
import type { DueItem } from '../push/localReminderStore'

/** Due browser-channel reminders since the given ISO timestamp (server clamps to the last hour). */
export async function getDueReminders(sinceUtc: string): Promise<DueItem[]> {
  const { data, error } = await api.GET('/api/reminders/due', {
    params: { query: { sinceUtc } },
  })
  if (error || !data) return []
  return (data.items ?? []).map((i) => ({
    reminderId: String(i.reminderId),
    occurrenceStartUtc: String(i.occurrenceStartUtc),
    title: i.title,
    location: i.location ?? null,
  }))
}
