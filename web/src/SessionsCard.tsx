import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { listSessions, revokeAllSessions, revokeOtherSessions, revokeSession } from './api/sessions'
import ConfirmDialog from './components/ConfirmDialog'
import { describeDevice } from './lib/devices'
import { formatDateMedium, formatRelative } from './lib/dates'

/**
 * Every browser and app signed in to this account, with a way to end any of them. Signing a
 * session out takes effect immediately — its refresh token and the access tokens it already
 * holds stop working on the next request.
 */
export default function SessionsCard({ onSignedOutEverywhere }: { onSignedOutEverywhere: () => void }) {
  const queryClient = useQueryClient()
  const { data: sessions = [], isLoading } = useQuery({ queryKey: ['sessions'], queryFn: listSessions })
  const [confirmAll, setConfirmAll] = useState(false)
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['sessions'] })
  const fail = (e: Error) => setNotice({ ok: false, text: e.message })
  const revokeOne = useMutation({ mutationFn: revokeSession, onSuccess: refresh, onError: fail })
  const revokeOthers = useMutation({
    mutationFn: revokeOtherSessions,
    onSuccess: (count) => {
      setNotice({ ok: true, text: count === 1 ? 'Signed out 1 other session.' : `Signed out ${count} other sessions.` })
      refresh()
    },
    onError: fail,
  })
  const revokeAll = useMutation({ mutationFn: revokeAllSessions, onSuccess: onSignedOutEverywhere, onError: fail })

  const others = sessions.filter((s) => !s.current)

  return (
    <div className="settings-card">
      <h2>Signed-in devices</h2>
      <p className="settings-sub">
        Every browser signed in to your account. Signing one out ends it right away. Calendar apps
        syncing over CalDAV use your password instead — change it to cut those off.
      </p>

      {isLoading ? (
        <p className="settings-note">Loading…</p>
      ) : (
        <ul className="cal-list">
          {sessions.map((s) => (
            <li key={s.id} className="cal-item session-item">
              <div className="cal-item-body">
                <span className="cal-item-name">
                  {describeDevice(s.userAgent)}
                  {s.current && <span className="badge-soon">This device</span>}
                </span>
                <span className="session-meta">
                  {s.current ? 'Active now' : `Active ${formatRelative(new Date(s.lastActiveAt))}`}
                  {' · '}signed in {formatDateMedium(new Date(s.startedAt))}
                  {s.ipAddress ? ` · ${s.ipAddress}` : ''}
                </span>
              </div>
              {!s.current && (
                <button
                  type="button"
                  className="btn-ghost"
                  disabled={revokeOne.isPending}
                  onClick={() => revokeOne.mutate(s.id)}
                >
                  Sign out
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      <div className="modal-actions">
        <button
          type="button"
          className="btn-ghost"
          disabled={others.length === 0 || revokeOthers.isPending}
          onClick={() => revokeOthers.mutate()}
        >
          Sign out all other devices
        </button>
        <span className="spacer" />
        <button type="button" className="btn-danger" onClick={() => setConfirmAll(true)}>
          Sign out everywhere
        </button>
      </div>

      {notice && <p className={notice.ok ? 'mail-notice-ok' : 'error'}>{notice.text}</p>}

      {confirmAll && (
        <ConfirmDialog
          title="Sign out everywhere"
          message="Every browser signed in to your account — this one included — will be signed out."
          confirmLabel="Sign out everywhere"
          onConfirm={() => {
            setConfirmAll(false)
            revokeAll.mutate()
          }}
          onClose={() => setConfirmAll(false)}
        />
      )}
    </div>
  )
}
