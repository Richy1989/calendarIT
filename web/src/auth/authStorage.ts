import type { components } from '../api/schema'

/** The token pair returned by the auth endpoints (generated from the API's OpenAPI spec). */
export type AuthTokens = components['schemas']['AuthTokens']

const STORAGE_KEY = 'calendarit.tokens'

export function getTokens(): AuthTokens | null {
  const raw = localStorage.getItem(STORAGE_KEY)
  if (!raw) return null
  try {
    return JSON.parse(raw) as AuthTokens
  } catch {
    return null
  }
}

export function setTokens(tokens: AuthTokens | null): void {
  if (tokens) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(tokens))
  } else {
    localStorage.removeItem(STORAGE_KEY)
  }
}

export function getAccessToken(): string | null {
  return getTokens()?.accessToken ?? null
}

/**
 * Calls `onChange` when another tab signs in or out (or refreshes the pair). Storage events only
 * fire in the *other* tabs, which is exactly the point: a sign-out anywhere signs out everywhere,
 * instead of leaving a tab that looks signed in until its next request fails.
 */
export function onTokensChangedElsewhere(onChange: (tokens: AuthTokens | null) => void): () => void {
  const listener = (e: StorageEvent) => {
    if (e.key === STORAGE_KEY || e.key === null) onChange(getTokens())
  }
  window.addEventListener('storage', listener)
  return () => window.removeEventListener('storage', listener)
}

/** The account a token pair belongs to: the access token's `sub` (the user id), or null if unreadable. */
export function accountOf(tokens: AuthTokens | null): string | null {
  const payload = tokens?.accessToken?.split('.')[1]
  if (!payload) return null
  try {
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(payload.length / 4) * 4, '='))
    const sub = (JSON.parse(json) as { sub?: unknown }).sub
    return typeof sub === 'string' && sub ? sub : null
  } catch {
    return null
  }
}

/**
 * What a token change made in another tab means for this one: `signedOut`; `same` (nothing new);
 * `refreshed` (the same account's next pair — swap it in, keep everything); or `switched` (another
 * account, or one this tab can't tell is the same) — everything this tab holds of the previous
 * account must go before the new pair is used. Storage events can arrive late and in a burst (a
 * background tab), so a sign-out followed by someone else's sign-in may show up as just the latter.
 */
export function tokenChange(current: AuthTokens | null, next: AuthTokens | null): 'signedOut' | 'same' | 'refreshed' | 'switched' {
  if (!next) return 'signedOut'
  if (current?.refreshToken === next.refreshToken && current.accessToken === next.accessToken) return 'same'
  const was = accountOf(current)
  return was !== null && was === accountOf(next) ? 'refreshed' : 'switched'
}
