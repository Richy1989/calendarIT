import { describe, expect, it } from 'vitest'
import { accountOf, tokenChange, type AuthTokens } from './authStorage'

const b64url = (o: object) => btoa(JSON.stringify(o)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
const pair = (sub: string, n: number): AuthTokens =>
  ({
    accessToken: `${b64url({ alg: 'HS256' })}.${b64url({ sub, n, email: 'ü@example.com' })}.sig`,
    accessTokenExpiresAt: '2026-01-01T00:00:00Z',
    refreshToken: `r-${sub}-${n}`,
    refreshTokenExpiresAt: '2026-01-02T00:00:00Z',
  }) as AuthTokens

describe('tokenChange', () => {
  const alice1 = pair('alice-id', 1)
  const alice2 = pair('alice-id', 2)
  const bob = pair('bob-id', 1)

  it('reads the account from the access token', () => {
    expect(accountOf(alice1)).toBe('alice-id')
    expect(accountOf(null)).toBeNull()
    expect(accountOf({ ...alice1, accessToken: 'garbage' })).toBeNull()
  })

  it('keeps state across a refresh of the same account', () => {
    expect(tokenChange(alice1, alice2)).toBe('refreshed')
    expect(tokenChange(alice1, alice1)).toBe('same')
  })

  it('treats a sign-out then another sign-in, seen only as the latter, as a switch', () => {
    expect(tokenChange(alice1, bob)).toBe('switched')
    expect(tokenChange(alice1, null)).toBe('signedOut')
  })

  it('treats a pair it cannot attribute as a switch', () => {
    expect(tokenChange(null, alice1)).toBe('switched')
    expect(tokenChange({ ...alice1, accessToken: 'x' }, alice2)).toBe('switched')
  })
})
