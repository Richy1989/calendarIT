import { api } from './client'
import type { components } from './schema'
import { ensureAccessToken } from '../auth/session'

export type ProfileDto = components['schemas']['ProfileDto']

export async function getProfile(): Promise<ProfileDto> {
  const { data, error } = await api.GET('/api/profile')
  if (error || !data) throw new Error('Failed to load profile')
  return data
}

export async function uploadAvatar(file: File): Promise<ProfileDto> {
  const token = await ensureAccessToken()
  const res = await fetch('/api/profile/avatar', {
    method: 'POST',
    headers: {
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      'Content-Type': file.type,
    },
    body: file,
  })
  if (!res.ok) throw new Error(res.status === 415 ? 'Please choose a PNG, JPEG, WebP, or GIF.' : 'Upload failed')
  return res.json()
}

export async function deleteAvatar(): Promise<void> {
  const { error } = await api.DELETE('/api/profile/avatar')
  if (error) throw new Error('Failed to remove picture')
}
