import { getTokens, setTokens } from './authStorage'

// Refresh the access token slightly before it actually expires.
const EXPIRY_SKEW_MS = 30_000

// Refresh tokens rotate: each one works exactly once. Every tab of the app shares one stored pair,
// so two tabs refreshing at the same moment would both send the same token — and the loser looked
// to the server like a stolen token being replayed. Refreshes therefore take a browser-wide lock.
const REFRESH_LOCK = 'calendarit-token-refresh'

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
      // Another tab may have rotated the pair meanwhile (a browser without Web Locks); if so, use
      // its fresh tokens rather than signing out.
      const current = getTokens()
      if (current && current.refreshToken !== refreshToken) return current.accessToken
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

/** One refresh at a time across every tab; whoever waited re-reads what the winner stored. */
function refreshExclusively(): Promise<string | null> {
  const run = async () => {
    const tokens = getTokens()
    if (!tokens) return null
    if (!isExpiringSoon(tokens.accessTokenExpiresAt)) return tokens.accessToken // another tab just did it
    return refresh(tokens.refreshToken)
  }
  return typeof navigator !== 'undefined' && navigator.locks?.request
    ? navigator.locks.request(REFRESH_LOCK, run)
    : run()
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
    refreshInFlight = refreshExclusively().finally(() => {
      refreshInFlight = null
    })
  }
  return refreshInFlight
}

/**
 * Signs this browser out: forgets the tokens locally and ends the session on the server, so the
 * refresh token can't be used again even if it was copied somewhere. Best effort on the network
 * side — being offline must not stop anyone from signing out locally.
 */
export async function signOut(): Promise<void> {
  const tokens = getTokens()
  setTokens(null)
  if (!tokens) return
  try {
    await fetch('/api/auth/logout', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: tokens.refreshToken }),
      keepalive: true,
    })
  } catch {
    // offline: the session simply expires on its own
  }
}

/**
 * Authorization header for calls made with raw `fetch` (file uploads/downloads and the
 * endpoints with no generated client). Refreshes the access token first when it's about to
 * expire, exactly like the openapi-fetch middleware does.
 */
export async function authHeaders(): Promise<Record<string, string>> {
  const token = await ensureAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}
