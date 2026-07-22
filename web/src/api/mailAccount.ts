import { api } from './client'
import type { components } from './schema'

export type MailAccountDto = components['schemas']['MailAccountDto']
export type SaveMailAccountRequest = components['schemas']['SaveMailAccountRequest']
export type MailTestResult = components['schemas']['MailTestResult']

/** The user's mail account, or null when none is configured yet. */
export async function getMailAccount(): Promise<MailAccountDto | null> {
  const { data, error, response } = await api.GET('/api/mail-account')
  if (response?.status === 404) return null
  if (error || !data) throw new Error('Failed to load the email account')
  return data
}

export async function saveMailAccount(body: SaveMailAccountRequest): Promise<MailAccountDto> {
  const { data, error } = await api.PUT('/api/mail-account', { body })
  if (error || !data) throw new Error('Failed to save the email account')
  return data
}

export async function deleteMailAccount(): Promise<void> {
  const { error, response } = await api.DELETE('/api/mail-account')
  if (error && response?.status !== 404) throw new Error('Failed to disconnect the email account')
}

export async function testMailAccount(): Promise<MailTestResult> {
  const { data, error } = await api.POST('/api/mail-account/test')
  if (error || !data) throw new Error('Connection test failed to run')
  return data
}
