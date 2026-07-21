import { getTokens, setTokens } from './authStorage'

// Refresh the access token slightly before it actually expires.
const EXPIRY_SKEW_MS = 30_000

let refreshInFlight: Promise<string | null> | null = null

function isExpiringSoon(expiresAtIso: string): boolean {
  const expiresAt = Date.parse(expiresAtIso)
  return Number.isNaN(expiresAt) || expiresAt - Date.now() < EXPIRY_SKEW_MS
}

async function refresh(refreshToken: string): Promise<string | null> {
  try {
    const res = await fetch('/api/auth/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
    if (!res.ok) {
      // Refresh token is invalid/expired — force a fresh login.
      setTokens(null)
      window.dispatchEvent(new Event('auth-expired'))
      return null
    }
    const next = await res.json()
    setTokens(next)
    return next.accessToken as string
  } catch {
    return null
  }
}

/**
 * Returns a valid access token, transparently refreshing it (via the rotating refresh
 * token) when it's expired or about to expire. Concurrent callers share one refresh.
 */
export async function ensureAccessToken(): Promise<string | null> {
  const tokens = getTokens()
  if (!tokens) return null
  if (!isExpiringSoon(tokens.accessTokenExpiresAt)) return tokens.accessToken

  if (!refreshInFlight) {
    refreshInFlight = refresh(tokens.refreshToken).finally(() => {
      refreshInFlight = null
    })
  }
  return refreshInFlight
}
