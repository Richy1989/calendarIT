import { api } from './client'
import type { components } from './schema'

export type SessionDto = components['schemas']['SessionDto']

/** This user's signed-in sessions (devices/browsers), the current one first. */
export async function listSessions(): Promise<SessionDto[]> {
  const { data, error } = await api.GET('/api/auth/sessions')
  if (error || !data) throw new Error('Failed to load your sessions')
  return data
}

/** Signs one session out; its tokens stop working at once. */
export async function revokeSession(id: string): Promise<void> {
  const { error, response } = await api.DELETE('/api/auth/sessions/{id}', { params: { path: { id } } })
  if (error && response?.status !== 404) throw new Error('Failed to sign that session out')
}

/** Signs out every session but this one; resolves to how many were signed out. */
export async function revokeOtherSessions(): Promise<number> {
  const { data, error } = await api.POST('/api/auth/sessions/revoke-others')
  if (error || !data) throw new Error('Failed to sign out the other sessions')
  return Number(data.count)
}

/** Signs out everywhere, this session included. */
export async function revokeAllSessions(): Promise<void> {
  const { error } = await api.POST('/api/auth/sessions/revoke-all')
  if (error) throw new Error('Failed to sign out everywhere')
}
