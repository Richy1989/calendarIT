import { describe, expect, it, vi } from 'vitest'

vi.mock('./client', () => ({
  api: { GET: vi.fn() },
}))

import { api } from './client'
import { getDueReminders } from './reminders'

describe('getDueReminders', () => {
  it('returns null when the request errors', async () => {
    vi.mocked(api.GET).mockResolvedValueOnce({ error: { message: 'boom' }, data: undefined } as never)
    const result = await getDueReminders('2026-08-01T00:00:00.000Z')
    expect(result).toBeNull()
  })

  it('returns mapped items on success', async () => {
    vi.mocked(api.GET).mockResolvedValueOnce({
      error: undefined,
      data: {
        items: [
          { reminderId: 1, occurrenceStartUtc: '2026-08-01T12:00:00Z', title: 'Standup', location: null },
        ],
      },
    } as never)
    const result = await getDueReminders('2026-08-01T00:00:00.000Z')
    expect(result).toEqual([
      { reminderId: '1', occurrenceStartUtc: '2026-08-01T12:00:00Z', title: 'Standup', location: null },
    ])
  })
})
