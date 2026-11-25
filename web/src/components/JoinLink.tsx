import type { MeetingLink } from '../lib/meetingLink'

/** "Join" for an appointment's video call: opens the meeting in a new tab. */
export default function JoinLink({ meeting, compact }: { meeting: MeetingLink; compact?: boolean }) {
  return (
    <a
      className={'join-link' + (compact ? ' is-compact' : '')}
      href={meeting.url}
      target="_blank"
      rel="noopener noreferrer"
      title={`Join on ${meeting.service}`}
      onClick={(e) => e.stopPropagation()}
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <rect x="3" y="6" width="12" height="12" rx="2.5" />
        <path d="M15 10.5l5-3v9l-5-3z" />
      </svg>
      {compact ? 'Join' : `Join ${meeting.service}`}
    </a>
  )
}
