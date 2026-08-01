import { describe, expect, it } from 'vitest'
import { decideNotifyMode } from './webPush'

describe('decideNotifyMode', () => {
  it('is denied when permission is not granted', () => {
    expect(decideNotifyMode({ permission: 'denied', pushSupported: true, pushSubscribed: true })).toBe('denied')
    expect(decideNotifyMode({ permission: 'default', pushSupported: true, pushSubscribed: false })).toBe('denied')
  })

  it('is push when granted and a push subscription exists', () => {
    expect(decideNotifyMode({ permission: 'granted', pushSupported: true, pushSubscribed: true })).toBe('push')
  })

  it('falls back to local when granted but push could not subscribe', () => {
    expect(decideNotifyMode({ permission: 'granted', pushSupported: true, pushSubscribed: false })).toBe('local')
    expect(decideNotifyMode({ permission: 'granted', pushSupported: false, pushSubscribed: false })).toBe('local')
  })
})
