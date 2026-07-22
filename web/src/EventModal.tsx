import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getMailAccount } from './api/mailAccount'

export type EventDraft = {
  id?: string
  /** Target calendar; undefined = the server picks the default. */
  calendarId?: string
  title: string
  /** 'YYYY-MM-DD' when all-day, otherwise 'YYYY-MM-DDTHH:mm'. */
  start: string
  end: string
  allDay: boolean
  /** Hex color. Default swatches are CSS3 named colors so they sync losslessly via
   *  the iCalendar COLOR property (RFC 7986); custom hex snaps to the nearest name. */
  color: string
  location: string
  description: string
  /** iCalendar RRULE, or '' for a one-off event. */
  recurrence: string
  reminders: { minutesBefore: number; channel: string }[]
  /** Guests to invite by email. Status is read-only (set by their replies). */
  attendees: { email: string; name?: string | null; status?: string }[]
}

const REMINDER_PRESETS: { label: string; value: number }[] = [
  { label: 'At start of event', value: 0 },
  { label: '5 minutes before', value: 5 },
  { label: '10 minutes before', value: 10 },
  { label: '15 minutes before', value: 15 },
  { label: '30 minutes before', value: 30 },
  { label: '1 hour before', value: 60 },
  { label: '2 hours before', value: 120 },
  { label: '1 day before', value: 1440 },
  { label: '1 week before', value: 10080 },
]

const REPEATS: { label: string; value: string }[] = [
  { label: 'Does not repeat', value: '' },
  { label: 'Daily', value: 'FREQ=DAILY' },
  { label: 'Every weekday (Mon–Fri)', value: 'FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR' },
  { label: 'Weekly', value: 'FREQ=WEEKLY' },
  { label: 'Monthly', value: 'FREQ=MONTHLY' },
  { label: 'Yearly', value: 'FREQ=YEARLY' },
]

// Each swatch value is an exact CSS3 color name's hex, for lossless CalDAV COLOR sync.
const SWATCHES: { name: string; hex: string }[] = [
  { name: 'Indigo', hex: '#7B68EE' }, // mediumslateblue
  { name: 'Blue', hex: '#6495ED' }, // cornflowerblue
  { name: 'Turquoise', hex: '#40E0D0' }, // turquoise
  { name: 'Green', hex: '#3CB371' }, // mediumseagreen
  { name: 'Amber', hex: '#DAA520' }, // goldenrod
  { name: 'Rose', hex: '#DB7093' }, // palevioletred
  { name: 'Tomato', hex: '#FF6347' }, // tomato
  { name: 'Slate', hex: '#708090' }, // slategray
]

const cssVar = (hex: string) => ({ ['--sw']: hex }) as React.CSSProperties

export default function EventModal({
  draft,
  calendars = [],
  onSave,
  onDelete,
  onClose,
}: {
  draft: EventDraft
  /** The user's calendars; the picker only shows when there is more than one. */
  calendars?: { id: string; name: string }[]
  onSave: (draft: EventDraft) => void
  onDelete?: (id: string) => void
  onClose: () => void
}) {
  const [title, setTitle] = useState(draft.title)
  const [calendarId, setCalendarId] = useState(draft.calendarId ?? calendars[0]?.id)
  const [allDay, setAllDay] = useState(draft.allDay)
  const [start, setStart] = useState(draft.start)
  const [end, setEnd] = useState(draft.end)
  const [color, setColor] = useState(draft.color)
  const [recurrence, setRecurrence] = useState(draft.recurrence)
  const [reminders, setReminders] = useState(draft.reminders)
  const [location, setLocation] = useState(draft.location)

  const addReminder = () => setReminders((rs) => [...rs, { minutesBefore: 15, channel: 'Email' }])
  const removeReminder = (i: number) => setReminders((rs) => rs.filter((_, idx) => idx !== i))
  const setReminderOffset = (i: number, minutesBefore: number) =>
    setReminders((rs) => rs.map((r, idx) => (idx === i ? { ...r, minutesBefore } : r)))
  const [description, setDescription] = useState(draft.description)
  const [attendees, setAttendees] = useState(draft.attendees)
  const [guestInput, setGuestInput] = useState('')
  const [expanded, setExpanded] = useState(Boolean(draft.location || draft.description || draft.attendees.length))
  const isEdit = Boolean(draft.id)

  // Invitations are sent from the user's own mailbox; without one they're saved but not mailed.
  const { data: mailAccount } = useQuery({ queryKey: ['mail-account'], queryFn: getMailAccount, staleTime: 60_000 })

  const addGuest = () => {
    const email = guestInput.trim().replace(/[,;]$/, '')
    if (!email) return
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) return // not a mail address (yet) — keep typing
    if (!attendees.some((a) => a.email.toLowerCase() === email.toLowerCase())) {
      setAttendees((list) => [...list, { email }])
    }
    setGuestInput('')
  }

  const removeGuest = (email: string) =>
    setAttendees((list) => list.filter((a) => a.email !== email))

  const onGuestKey = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault()
      addGuest()
    } else if (e.key === 'Backspace' && !guestInput && attendees.length) {
      removeGuest(attendees[attendees.length - 1].email)
    }
  }

  const statusMark = (status?: string) =>
    status === 'Accepted' ? { mark: '✓', cls: ' ok' } :
    status === 'Declined' ? { mark: '✕', cls: ' no' } :
    status === 'Tentative' ? { mark: '?', cls: '' } : null
  const isCustom = !SWATCHES.some((s) => s.hex.toLowerCase() === color.toLowerCase())

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  // Keep the date strings in a format matching the active input type.
  const toggleAllDay = (checked: boolean) => {
    if (checked) {
      setStart((s) => s.slice(0, 10))
      setEnd((s) => s.slice(0, 10))
    } else {
      setStart((s) => (s.length <= 10 ? `${s}T09:00` : s))
      setEnd((s) => (s.length <= 10 ? `${s}T10:00` : s))
    }
    setAllDay(checked)
  }

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!title.trim()) return
    onSave({ id: draft.id, calendarId, title: title.trim(), start, end, allDay, color, location, description, recurrence, reminders, attendees })
  }

  const inputType = allDay ? 'date' : 'datetime-local'

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit}>
        <div className="modal-head">
          <span className="eyebrow">{isEdit ? 'Edit appointment' : 'New appointment'}</span>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        <div className="field">
          <label htmlFor="ev-title">Title</label>
          {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
          <input
            id="ev-title"
            value={title}
            autoFocus
            placeholder="What's happening?"
            onChange={(e) => setTitle(e.target.value)}
          />
        </div>

        <label className="toggle">
          <input type="checkbox" checked={allDay} onChange={(e) => toggleAllDay(e.target.checked)} />
          <span>All day</span>
        </label>

        <div className="field-row">
          <div className="field">
            <label htmlFor="ev-start">Starts</label>
            <input id="ev-start" type={inputType} value={start} required onChange={(e) => setStart(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="ev-end">Ends</label>
            <input id="ev-end" type={inputType} value={end} required onChange={(e) => setEnd(e.target.value)} />
          </div>
        </div>

        {calendars.length > 1 && (
          <div className="field">
            <label htmlFor="ev-calendar">Calendar</label>
            <select id="ev-calendar" value={calendarId} onChange={(e) => setCalendarId(e.target.value)}>
              {calendars.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className="field">
          <label htmlFor="ev-repeat">Repeats</label>
          <select id="ev-repeat" value={recurrence} onChange={(e) => setRecurrence(e.target.value)}>
            {REPEATS.map((r) => (
              <option key={r.value || 'none'} value={r.value}>
                {r.label}
              </option>
            ))}
            {recurrence !== '' && !REPEATS.some((r) => r.value === recurrence) && (
              <option value={recurrence}>Custom rule</option>
            )}
          </select>
          {isEdit && recurrence !== '' && <p className="field-hint">Saving updates the whole series.</p>}
        </div>

        <div className="field">
          <label>Reminders</label>
          <div className="reminders">
            {reminders.map((r, i) => (
              <div className="reminder-row" key={i}>
                <select value={r.minutesBefore} onChange={(e) => setReminderOffset(i, Number(e.target.value))}>
                  {REMINDER_PRESETS.map((p) => (
                    <option key={p.value} value={p.value}>
                      {p.label}
                    </option>
                  ))}
                </select>
                <span className="reminder-via">email</span>
                <button type="button" className="reminder-remove" onClick={() => removeReminder(i)} aria-label="Remove reminder">
                  ✕
                </button>
              </div>
            ))}
            <button type="button" className="reminder-add" onClick={addReminder}>
              ＋ Add reminder
            </button>
          </div>
        </div>

        <div className="field">
          <label>Color</label>
          <div className="swatches">
            {SWATCHES.map((s) => (
              <button
                key={s.hex}
                type="button"
                className={'swatch' + (!isCustom && color.toLowerCase() === s.hex.toLowerCase() ? ' active' : '')}
                style={cssVar(s.hex)}
                title={s.name}
                aria-label={s.name}
                onClick={() => setColor(s.hex)}
              />
            ))}
            <label
              className={'swatch swatch-custom' + (isCustom ? ' active' : '')}
              style={cssVar(color)}
              title="Custom color"
            >
              <input type="color" value={color} onChange={(e) => setColor(e.target.value)} />
            </label>
          </div>
        </div>

        <button
          type="button"
          className="details-toggle"
          aria-expanded={expanded}
          onClick={() => setExpanded((v) => !v)}
        >
          <span className={'chev' + (expanded ? ' open' : '')} aria-hidden="true">
            ▸
          </span>
          {expanded ? 'Fewer details' : 'Add details'}
        </button>

        {expanded && (
          <div className="details">
            <div className="field">
              <label htmlFor="ev-location">Location</label>
              <input
                id="ev-location"
                value={location}
                placeholder="Where is it?"
                onChange={(e) => setLocation(e.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="ev-desc">Description</label>
              <textarea
                id="ev-desc"
                rows={3}
                value={description}
                placeholder="Notes, agenda, links…"
                onChange={(e) => setDescription(e.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="ev-guests">Guests</label>
              <div className="guest-chips">
                {attendees.map((a) => {
                  const s = statusMark(a.status)
                  return (
                    <span className="guest-chip" key={a.email} title={a.status && a.status !== 'NeedsAction' ? a.status : 'Awaiting reply'}>
                      {s && <span className={'guest-chip-status' + s.cls}>{s.mark}</span>}
                      {a.email}
                      <button type="button" className="guest-chip-remove" onClick={() => removeGuest(a.email)} aria-label={`Remove ${a.email}`}>
                        ✕
                      </button>
                    </span>
                  )
                })}
                <input
                  id="ev-guests"
                  value={guestInput}
                  placeholder={attendees.length ? 'Add another…' : 'Invite people by email'}
                  onChange={(e) => setGuestInput(e.target.value)}
                  onKeyDown={onGuestKey}
                  onBlur={addGuest}
                />
              </div>
              {attendees.length > 0 && mailAccount === null && (
                <p className="field-hint">
                  Guests are saved, but invitations aren't sent yet — connect an email account in
                  Settings → Email first.
                </p>
              )}
            </div>
          </div>
        )}

        <div className="modal-actions">
          {isEdit && onDelete && (
            <button type="button" className="btn-danger" onClick={() => onDelete(draft.id!)}>
              Delete
            </button>
          )}
          <span className="spacer" />
          <button type="button" className="btn-ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="btn-primary">
            {isEdit ? 'Save' : 'Add appointment'}
          </button>
        </div>
      </form>
    </div>
  )
}
