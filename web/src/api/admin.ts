import { api } from './client'
import type { components } from './schema'

export type PendingAccount = components['schemas']['PendingAccountDto']

/** Accounts waiting for activation, oldest first (admin only). */
export async function listPendingAccounts(): Promise<PendingAccount[]> {
  const { data, error } = await api.GET('/api/admin/pending-accounts')
  if (error || !data) throw new Error('Could not load the accounts waiting for activation.')
  return data
}

export async function activateAccount(id: string): Promise<void> {
  const { error } = await api.POST('/api/admin/pending-accounts/{id}/activate', { params: { path: { id } } })
  if (error) throw new Error('Could not activate the account.')
}

/** Removes an account that was never activated. */
export async function removePendingAccount(id: string): Promise<void> {
  const { error } = await api.DELETE('/api/admin/pending-accounts/{id}', { params: { path: { id } } })
  if (error) throw new Error('Could not remove the account.')
}
