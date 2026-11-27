import { api } from './client'
import type { components } from './schema'

export type UserAccount = components['schemas']['UserAccountDto']

/** The server's message for a refused action (409), else the fallback. */
function messageOf(error: unknown, fallback: string): string {
  const detail = (error as { detail?: unknown } | undefined)?.detail
  return typeof detail === 'string' && detail ? detail : fallback
}

/** Every account (admin only): waiting ones first. */
export async function listUsers(): Promise<UserAccount[]> {
  const { data, error } = await api.GET('/api/admin/users')
  if (error || !data) throw new Error('Could not load the accounts.')
  return data
}

export async function activateUser(id: string): Promise<void> {
  const { error } = await api.POST('/api/admin/users/{id}/activate', { params: { path: { id } } })
  if (error) throw new Error(messageOf(error, 'Could not activate the account.'))
}

export async function setUserDisabled(id: string, disabled: boolean): Promise<void> {
  const { error } = disabled
    ? await api.POST('/api/admin/users/{id}/disable', { params: { path: { id } } })
    : await api.POST('/api/admin/users/{id}/enable', { params: { path: { id } } })
  if (error) throw new Error(messageOf(error, disabled ? 'Could not disable the account.' : 'Could not enable the account.'))
}

/** Deletes the account and everything in it. */
export async function deleteUser(id: string): Promise<void> {
  const { error } = await api.DELETE('/api/admin/users/{id}', { params: { path: { id } } })
  if (error) throw new Error(messageOf(error, 'Could not delete the account.'))
}

export async function setUserAdmin(id: string, admin: boolean): Promise<void> {
  const { error } = admin
    ? await api.PUT('/api/admin/users/{id}/admin', { params: { path: { id } } })
    : await api.DELETE('/api/admin/users/{id}/admin', { params: { path: { id } } })
  if (error) throw new Error(messageOf(error, 'Could not change the admin rights.'))
}

/** A single-use password-reset link, absolute (the server sends a path when PUBLIC_BASE_URL isn't set). */
export async function createResetLink(id: string): Promise<string> {
  const { data, error } = await api.POST('/api/admin/users/{id}/reset-link', { params: { path: { id } } })
  if (error || !data) throw new Error(messageOf(error, 'Could not create a reset link.'))
  return new URL(data.link, window.location.origin).toString()
}
