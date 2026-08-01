/* Pure dedup for local reminder notifications: decide which due items are new, and keep a pruned
 * record of what's been shown so a reminder isn't re-shown on the next poll, another tab, or a
 * refresh. Kept free of DOM/storage so it can be unit-tested directly. */

export type DueItem = {
  reminderId: string
  occurrenceStartUtc: string
  title: string
  location?: string | null
}

export type ShownEntry = { key: string; at: number }

/** How long a shown-key is remembered. Comfortably longer than the poll/lookback window. */
export const RETENTION_MS = 2 * 60 * 60 * 1000

const keyOf = (item: DueItem) => `${item.reminderId}:${item.occurrenceStartUtc}`

export function selectToShow(
  shown: ShownEntry[],
  items: DueItem[],
  now: number,
): { toShow: DueItem[]; nextShown: ShownEntry[] } {
  const have = new Set(shown.map((e) => e.key))
  const toShow: DueItem[] = []
  const added: ShownEntry[] = []
  for (const item of items) {
    const key = keyOf(item)
    if (!have.has(key)) {
      have.add(key)
      toShow.push(item)
      added.push({ key, at: now })
    }
  }
  const nextShown = [...shown, ...added].filter((e) => now - e.at < RETENTION_MS)
  return { toShow, nextShown }
}
