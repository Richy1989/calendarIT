import { authHeaders } from '../auth/session'

// These call the API directly rather than through the generated client: the endpoints are new,
// and regenerating schema.d.ts needs a running backend. Hand-patching the generated file (as has
// happened before) quietly desynchronises it from the server, which is worse than a small
// hand-written module — this one is typed here and has no generated counterpart to drift from.

/** Config the sign-in screen needs before anyone has signed in. `emailConfirmationAvailable`:
 *  the server can mail confirmation links (it has a mail account of its own). */
export type AuthConfig = { registrationEnabled: boolean; emailConfirmationAvailable?: boolean }

async function fail(response: Response, fallback: string): Promise<never> {
  // The API reports problems as { errors: string[] }; show the first, which is the actionable one.
  let message = fallback
  try {
    const body = (await response.json()) as { errors?: unknown }
    if (Array.isArray(body.errors) && body.errors.length > 0) {
      message = String(body.errors[0])
    }
  } catch {
    // no JSON body — keep the fallback
  }
  throw new Error(message)
}

/** Whether this instance still accepts new accounts. Assumes yes if the call fails, so a
 *  hiccup can't make the Register tab vanish on an instance that does allow sign-up. */
export async function getAuthConfig(): Promise<AuthConfig> {
  try {
    const response = await fetch('/api/auth/config')
    if (!response.ok) return { registrationEnabled: true }
    return (await response.json()) as AuthConfig
  } catch {
    return { registrationEnabled: true }
  }
}

/** Confirms the address from the link in the confirmation mail. Works signed in or out. */
export async function confirmEmail(email: string, token: string): Promise<void> {
  const response = await fetch('/api/auth/confirm-email', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, token }),
  })
  if (!response.ok) {
    await fail(response, 'Could not confirm the email address.')
  }
}

/** Mails a new activation link to an account waiting for activation. Nobody is signed in, so it
 *  resolves the same way whether or not the address has such an account. */
export async function resendActivation(email: string): Promise<void> {
  const response = await fetch('/api/auth/confirm-email/resend', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email }),
  })
  if (!response.ok) {
    let message = 'Could not send the activation email.'
    try {
      const body = (await response.json()) as { detail?: unknown }
      if (typeof body.detail === 'string') message = body.detail
    } catch {
      // keep the fallback
    }
    throw new Error(message)
  }
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  const response = await fetch('/api/auth/change-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({ currentPassword, newPassword }),
  })
  if (!response.ok) {
    await fail(response, 'Could not change the password.')
  }
}

/** Starts recovery. Resolves the same way whether or not the address has an account. */
export async function requestPasswordReset(email: string): Promise<void> {
  const response = await fetch('/api/auth/forgot-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email }),
  })
  if (!response.ok) {
    await fail(response, 'Could not send the reset email.')
  }
}

export async function resetPassword(email: string, token: string, newPassword: string): Promise<void> {
  const response = await fetch('/api/auth/reset-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, token, newPassword }),
  })
  if (!response.ok) {
    await fail(response, 'Could not reset the password.')
  }
}
