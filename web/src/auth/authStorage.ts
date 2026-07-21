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
