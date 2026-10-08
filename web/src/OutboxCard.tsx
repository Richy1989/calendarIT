import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { discardOutbox, listOutbox, retryOutbox, type OutboxItemDto } from './api/mailAccount'
import { formatRelative } from './lib/dates'

const KIND_LABEL: Record<string, string> = {
  Invitation: 'Invitation',
  Reply: 'RSVP',
  Reminder: 'Reminder',
  PasswordReset: 'Password reset',
}

/** What happened to a message, in a few words. */
function statusLine(m: OutboxItemDto): string {
  if (m.status === 'Sent') return m.sentAt ? `Sent ${formatRelative(new Date(m.sentAt))}` : 'Sent'
  if (m.status === 'Failed') return 'Not sent'
  if (Number(m.attempts) === 0) return 'Sending…'
  return m.nextAttemptAt ? `Retrying ${formatRelative(new Date(m.nextAttemptAt))}` : 'Retrying'
}

/**
 * Mail goes out through the user's own account in the background, retrying while their mail
 * server is unreachable. This is where they can see that happening — and see, rather than guess,
 * why an invitation never arrived.
 */
export default function OutboxCard() {
  const queryClient = useQueryClient()
  const { data: items = [] } = useQuery({
    queryKey: ['outbox'],
    queryFn: listOutbox,
    // Queued mail usually leaves within seconds; keep the list honest while it's on screen.
    refetchInterval: 15_000,
  })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['outbox'] })
  const retry = useMutation({ mutationFn: retryOutbox, onSettled: refresh })
  const discard = useMutation({ mutationFn: discardOutbox, onSettled: refresh })

  return (
    <div className="settings-card">
      <h2>Outgoing mail</h2>
      <p className="settings-sub">
        Invitations, replies and reminders go out from your account in the background. If your mail
        server can't be reached they're retried for a while, then listed here as not sent.
      </p>

      {items.length === 0 ? (
        <p className="settings-note">Nothing sent yet.</p>
      ) : (
        <ul className="cal-list">
          {items.map((m) => (
            <li key={m.id} className="cal-item session-item">
              <div className="cal-item-body">
                <span className="cal-item-name">
                  {m.subject || '(no subject)'}
                  <span className="badge-soon">{KIND_LABEL[m.kind] ?? m.kind}</span>
                </span>
                <span className={'session-meta' + (m.status === 'Failed' ? ' outbox-failed' : '')}>
                  To {m.recipient} · {statusLine(m)}
                  {m.lastError && m.status !== 'Sent' ? ` — ${m.lastError}` : ''}
                </span>
              </div>
              {m.canRetry && (
                <button type="button" className="btn-ghost" disabled={retry.isPending} onClick={() => retry.mutate(m.id)}>
                  Retry
                </button>
              )}
              {m.status !== 'Sent' && (
                <button type="button" className="btn-ghost" disabled={discard.isPending} onClick={() => discard.mutate(m.id)}>
                  Discard
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
