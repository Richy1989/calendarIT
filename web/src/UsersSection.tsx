import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  activateUser, createResetLink, deleteUser, listUsers, setUserAdmin, setUserDisabled, type UserAccount,
} from './api/admin'
import ConfirmDialog from './components/ConfirmDialog'
import { formatRelative } from './lib/dates'

type Filter = 'all' | 'waiting' | 'disabled'

/** What a confirmation dialog is about to do. */
type Pending = { title: string; message: string; label: string; run: () => Promise<void> }

/**
 * The admin's user page: every account on the server, with what can be done to it. The admin's own
 * row offers nothing that would lock them out (the server refuses it too).
 */
export default function UsersSection() {
  const queryClient = useQueryClient()
  const { data: users = [], isLoading, error: loadError } = useQuery({ queryKey: ['admin-users'], queryFn: listUsers })
  const [filter, setFilter] = useState<Filter>('all') // waiting accounts are listed first anyway
  const [confirm, setConfirm] = useState<Pending | null>(null)
  const [resetLink, setResetLink] = useState<{ email: string; link: string } | null>(null)
  const [copied, setCopied] = useState(false)

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin-users'] })
    void queryClient.invalidateQueries({ queryKey: ['profile'] }) // the badge's count of waiting accounts
  }
  const action = useMutation({ mutationFn: (run: () => Promise<void>) => run(), onSuccess: refresh })
  const reset = useMutation({
    mutationFn: (u: UserAccount) => createResetLink(u.id).then((link) => ({ email: u.email, link })),
    onSuccess: (r) => {
      setCopied(false)
      setResetLink(r)
    },
  })

  const waiting = users.filter((u) => !u.active).length
  const disabled = users.filter((u) => u.disabled).length
  const shown = users.filter((u) => (filter === 'waiting' ? !u.active : filter === 'disabled' ? u.disabled : true))
  const busy = action.isPending || reset.isPending
  const error = action.error ?? reset.error ?? loadError

  return (
    <div className="settings-card">
      <h2>Users</h2>
      <p className="settings-sub">
        Everyone with an account on this server. New sign-ups wait here until you activate them; a disabled account is
        signed out everywhere and can't sign in, but keeps its data.
      </p>

      <div className="segmented users-filter" role="tablist">
        {(
          [
            ['all', `All (${users.length})`],
            ['waiting', `Waiting (${waiting})`],
            ['disabled', `Disabled (${disabled})`],
          ] as const
        ).map(([key, label]) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={filter === key}
            className={filter === key ? 'active' : ''}
            onClick={() => setFilter(key)}
          >
            {label}
          </button>
        ))}
      </div>

      {!isLoading && shown.length === 0 && (
        <p className="field-hint">{filter === 'waiting' ? 'Nobody is waiting.' : filter === 'disabled' ? 'No disabled accounts.' : 'No accounts.'}</p>
      )}

      <ul className="users-list">
        {shown.map((u) => (
          <li key={u.id} className="users-row">
            <div className="users-main">
              <span className="users-email">{u.email}</span>
              <span className="users-badges">
                {u.isYou && <span className="users-badge">You</span>}
                {u.isAdmin && <span className="users-badge users-badge-admin">Admin</span>}
                {!u.active && <span className="users-badge users-badge-waiting">Waiting</span>}
                {u.disabled && <span className="users-badge users-badge-disabled">Disabled</span>}
              </span>
              <span className="field-hint">
                {u.createdAt ? `Signed up ${new Date(u.createdAt).toLocaleDateString()}` : 'Signed up before this was recorded'}
                {' · '}
                {u.lastActiveAt ? `active ${formatRelative(new Date(u.lastActiveAt))}` : 'not active yet'}
              </span>
            </div>
            <div className="users-actions">
              {!u.active && (
                <button type="button" className="btn-primary" disabled={busy} onClick={() => action.mutate(() => activateUser(u.id))}>
                  Activate
                </button>
              )}
              {u.active && (
                <button type="button" className="btn-ghost" disabled={busy} onClick={() => reset.mutate(u)}>
                  Reset link
                </button>
              )}
              {!u.isYou && u.active && !u.disabled && (
                <button
                  type="button"
                  className="btn-ghost"
                  disabled={busy}
                  onClick={() =>
                    u.isAdmin
                      ? setConfirm({
                          title: 'Remove admin rights',
                          message: `${u.email} will no longer be able to manage accounts.`,
                          label: 'Remove admin',
                          run: () => setUserAdmin(u.id, false),
                        })
                      : setConfirm({
                          title: 'Make admin',
                          message: `${u.email} will be able to activate, disable and delete accounts — yours included.`,
                          label: 'Make admin',
                          run: () => setUserAdmin(u.id, true),
                        })
                  }
                >
                  {u.isAdmin ? 'Remove admin' : 'Make admin'}
                </button>
              )}
              {!u.isYou && u.active && (
                <button
                  type="button"
                  className="btn-ghost"
                  disabled={busy}
                  onClick={() =>
                    u.disabled
                      ? action.mutate(() => setUserDisabled(u.id, false))
                      : setConfirm({
                          title: 'Disable account',
                          message: `${u.email} will be signed out on every device and can't sign in until you enable the account again. Nothing is deleted.`,
                          label: 'Disable',
                          run: () => setUserDisabled(u.id, true),
                        })
                  }
                >
                  {u.disabled ? 'Enable' : 'Disable'}
                </button>
              )}
              {!u.isYou && (
                <button
                  type="button"
                  className="btn-ghost users-delete"
                  disabled={busy}
                  onClick={() =>
                    setConfirm({
                      title: 'Delete account',
                      message: u.active
                        ? `Delete ${u.email} and all of its calendars, events, tasks and settings? This can't be undone.`
                        : `Remove the sign-up ${u.email}? It was never activated.`,
                      label: u.active ? 'Delete account' : 'Remove',
                      run: () => deleteUser(u.id),
                    })
                  }
                >
                  {u.active ? 'Delete' : 'Remove'}
                </button>
              )}
            </div>
          </li>
        ))}
      </ul>

      {resetLink && (
        <div className="users-reset">
          <p className="field-hint">
            Password-reset link for <b>{resetLink.email}</b>. It works once, for two hours. Hand it over privately — whoever
            opens it can set the password.
          </p>
          <div className="users-reset-row">
            <input readOnly value={resetLink.link} onFocus={(e) => e.currentTarget.select()} aria-label="Password-reset link" />
            <button
              type="button"
              className="btn-ghost"
              onClick={() => {
                void navigator.clipboard?.writeText(resetLink.link).then(() => setCopied(true))
              }}
            >
              {copied ? 'Copied' : 'Copy'}
            </button>
            <button type="button" className="btn-ghost" onClick={() => setResetLink(null)}>
              Done
            </button>
          </div>
        </div>
      )}

      {error && <p className="error">{(error as Error).message}</p>}

      {confirm && (
        <ConfirmDialog
          title={confirm.title}
          message={confirm.message}
          confirmLabel={confirm.label}
          onClose={() => setConfirm(null)}
          onConfirm={() => {
            const run = confirm.run
            setConfirm(null)
            action.mutate(run)
          }}
        />
      )}
    </div>
  )
}
