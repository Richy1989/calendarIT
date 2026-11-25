import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getMailAccount } from './api/mailAccount'
import { listCategories } from './api/categories'
import JoinLink from './components/JoinLink'
import { findMeetingLink } from './lib/meetingLink'
import CategoryPicker from './components/CategoryPicker'
import EventWhen from './components/EventWhen'
import { ColorDot, FormIcon } from './components/FormIcons'
import RecurrenceEditor from './components/RecurrenceEditor'
import { parseLocalValue, toLocalValue } from './lib/dates'
import { defaultRemindersFor, REMINDER_OFFSETS, repeatPresets, sameReminders } from './lib/eventForm'
import { buildRule, defaultSpec, describeRule, parseRule, weekdayOf, type RuleSpec } from './lib/rrule'
import { useFirstDay } from './weekStart'
import { enableNotifications } from './push/webPush'
import { startLocalReminderPoller } from './push/localReminders'

export type EventDraft = {
  id?: string
  /** Target calendar; undefined = the server picks the default. */
  calendarId?: string
  title: string
  /** 'YYYY-MM-DD' when all-day, otherwise 'YYYY-MM-DDTHH:mm'. */
  start: string
  end: string
  allDay: boolean
  /** The category (named color, managed in Settings) the event takes its color from;
   *  null = uncategorized (default color). */
  categoryId: string | null
  location: string
  description: string
  /** iCalendar RRULE, or '' for a one-off event. */
  recurrence: string
  reminders: { minutesBefore: number; channel: string }[]
  /** Guests to invite by email. Status is read-only (set by their replies). */
  attendees: { email: string; name?: string | null; status?: string }[]
  /** Set when this event is an invitation the user received: their RSVP status
   *  ("NeedsAction"/"Accepted"/…). Null/undefined for the user's own events. */
  invitationStatus?: string | null
  /** The organizer of a received invitation, shown in the RSVP banner. */
  organizerEmail?: string | null
  /** The zone an existing event lives in (null: none), sent back unchanged so an edit never
   *  moves it. Undefined on a new event, which takes the browser's. */
  timeZone?: string | null
  /** Set when the draft belongs to a repeating series: the series (master) id. */
  seriesId?: string
  /** With `seriesId`: the occurrence the editor was opened on — its start in the series' rule. */
  occurrence?: string
  /** What a save changes: just `occurrence`, or the whole series. */
  scope?: EditScope
  /** True when this occurrence already has its own edits. */
  isOverride?: boolean
  /** Opens the editor with the custom repeat editor showing ("Custom…" in the quick form). */
  customRepeat?: boolean
}

/** Which part of a repeating series an edit applies to. */
export type EditScope = 'occurrence' | 'series'

/** The Repeats select's "Custom…" entry (opens the repeat editor). Not a valid RRULE on purpose. */
const CUSTOM = '__custom__'

const isPreset = (rule: string) => repeatPresets(new Date()).some((r) => r.rule === rule)

/** Friendly labels for RSVP / invitation statuses. */
const RSVP_LABEL: Record<string, string> = {
  Accepted: 'Accept',
  Tentative: 'Maybe',
  Declined: 'Decline',
  NeedsAction: 'Pending',
}

/** Uncategorized events render in this neutral default. */
const NO_CATEGORY_COLOR = '#708090' // slategray

export default function EventModal({
  draft,
  calendars = [],
  onSave,
  onDelete,
  onCopy,
  onRespond,
  onScopeChange,
  onClose,
}: {
  draft: EventDraft
  /** The user's calendars; the picker only shows when there is more than one. */
  calendars?: {
    id: string
    name: string
    defaultCategoryId?: string | null
    defaultReminders?: { minutesBefore: number | string; channel: string }[] | null
  }[]
  onSave: (draft: EventDraft) => void
  /** Deletes what the editor shows: the occurrence or the series, per `draft.scope`. */
  onDelete?: () => void
  /** Present for a repeating event: switches the editor between one occurrence and the series. */
  onScopeChange?: (scope: EditScope) => void
  /** Copies this (saved) event to the clipboard. Present only for existing events. */
  onCopy?: (id: string) => void
  /** Present only for a received invitation: records the user's RSVP. */
  onRespond?: (status: 'Accepted' | 'Declined' | 'Tentative') => void
  onClose: () => void
}) {
  const [title, setTitle] = useState(draft.title)
  const [calendarId, setCalendarId] = useState(draft.calendarId ?? calendars[0]?.id)
  const [allDay, setAllDay] = useState(draft.allDay)
  const [start, setStart] = useState(draft.start)
  const [end, setEnd] = useState(draft.end)
  const [categoryId, setCategoryId] = useState(draft.categoryId)
  const [recurrence, setRecurrence] = useState(draft.recurrence)
  const [reminders, setReminders] = useState(draft.reminders)
  const [location, setLocation] = useState(draft.location)
  const firstDay = useFirstDay()

  // The custom-repeat editor's state, when it's open: set for a rule the presets don't cover (and
  // the editor can read), or once "Custom…" is picked. The RRULE text is only rebuilt from it when
  // something changes — the server takes a changed rule for a moved series and drops its exceptions.
  const [customSpec, setCustomSpec] = useState<RuleSpec | null>(() => {
    const at = parseLocalValue(draft.start, draft.allDay)
    if (draft.customRepeat) return (draft.recurrence && parseRule(draft.recurrence, at)) || defaultSpec(at)
    return draft.recurrence && !isPreset(draft.recurrence) ? parseRule(draft.recurrence, at) : null
  })
  const startDate = parseLocalValue(start, allDay)
  // "Custom…" from the quick form starts a rule; it's written once the editor has one.
  const customStarted = useRef(false)
  useEffect(() => {
    if (draft.customRepeat && customSpec && !customStarted.current) {
      customStarted.current = true
      setRecurrence(buildRule(customSpec, startDate, allDay))
    }
  }, [draft.customRepeat, customSpec, startDate, allDay])
  // A rule another app wrote, using parts the editor doesn't model: shown, kept, not editable here.
  const foreignRule = recurrence !== '' && !isPreset(recurrence) && !customSpec

  const applySpec = (spec: RuleSpec, at: Date = startDate, asAllDay: boolean = allDay) => {
    setCustomSpec(spec)
    setRecurrence(buildRule(spec, at, asAllDay))
  }

  const chooseRepeat = (value: string) => {
    if (value === CUSTOM) {
      applySpec((recurrence && parseRule(recurrence, startDate)) || defaultSpec(startDate))
      return
    }
    setCustomSpec(null)
    setRecurrence(value)
  }

  // Moving the start drags the end along by the same delta, keeping the duration fixed — and a
  // custom rule along with it ("on day 13", "the second Tuesday", the weekday it started on).
  const changeStart = (next: string) => {
    const nextDate = parseLocalValue(next, allDay)
    const delta = nextDate.getTime() - startDate.getTime()
    setStart(next)
    if (delta) setEnd(toLocalValue(new Date(parseLocalValue(end, allDay).getTime() + delta), allDay))
    if (customSpec && delta) {
      const onlyOwnDay = customSpec.byDay.length === 1 && customSpec.byDay[0] === weekdayOf(startDate)
      applySpec(onlyOwnDay ? { ...customSpec, byDay: [weekdayOf(nextDate)] } : customSpec, nextDate)
    }
  }

  const [pushHint, setPushHint] = useState<string | null>(null)
  const addReminder = () => setReminders((rs) => [...rs, { minutesBefore: 15, channel: 'Email' }])
  const removeReminder = (i: number) => setReminders((rs) => rs.filter((_, idx) => idx !== i))
  const setReminderOffset = (i: number, minutesBefore: number) =>
    setReminders((rs) => rs.map((r, idx) => (idx === i ? { ...r, minutesBefore } : r)))
  // Switching a reminder to Browser needs notifications enabled on this device first. This tries
  // real Web Push, and on a browser that can't subscribe (e.g. Brave with Google services off)
  // falls back to local mode — notifications shown while CalendarIT is open. Only if the user
  // blocks the permission (or the browser can't notify at all) do we keep the row on Email.
  const setReminderChannel = async (i: number, channel: string) => {
    if (channel === 'WebPush') {
      const result = await enableNotifications()
      if (result === 'denied' || result === 'unsupported') {
        setPushHint(
          result === 'denied'
            ? 'Browser notifications are blocked — enable them in your browser settings, then try again.'
            : "This browser can't show notifications.",
        )
        return
      }
      // 'push' or 'local' — the reminder can be delivered. In local mode this device only shows
      // notifications while CalendarIT is open, so start the poller now and say so.
      if (result === 'local') {
        startLocalReminderPoller()
        setPushHint('This browser will show reminders while CalendarIT is open in a tab or installed as an app.')
      } else {
        setPushHint(null)
      }
    }
    setReminders((rs) => rs.map((r, idx) => (idx === i ? { ...r, channel } : r)))
  }
  const [description, setDescription] = useState(draft.description)
  const meeting = findMeetingLink(location, description)
  const [attendees, setAttendees] = useState(draft.attendees)
  const [guestInput, setGuestInput] = useState('')
  const isEdit = Boolean(draft.id)

  // A new event follows its calendar's default reminders until they're changed by hand.
  const chooseCalendar = (id: string) => {
    if (!isEdit) {
      const before = defaultRemindersFor(calendars.find((c) => c.id === calendarId), allDay)
      if (sameReminders(reminders, before)) setReminders(defaultRemindersFor(calendars.find((c) => c.id === id), allDay))
    }
    setCalendarId(id)
  }
  // Repeat rule, calendar and guests belong to the series; editing one occurrence leaves them be.
  const occurrenceOnly = draft.scope === 'occurrence'

  // Invitations are sent from the user's own mailbox; without one they're saved but not mailed.
  const { data: mailAccount } = useQuery({ queryKey: ['mail-account'], queryFn: getMailAccount, staleTime: 60_000 })

  // Categories (named colors, managed in Settings) — the event takes its color from one.
  const { data: categories = [] } = useQuery({ queryKey: ['categories'], queryFn: listCategories, staleTime: 60_000 })
  // The category it takes when it has none of its own: its calendar's default.
  const inheritedId = calendars.find((c) => c.id === calendarId)?.defaultCategoryId
  const inherited = inheritedId ? categories.find((c) => c.id === inheritedId) : undefined
  const ownCategory = categoryId ? categories.find((c) => c.id === categoryId) : undefined
  const shownColor = ownCategory?.color ?? inherited?.color ?? NO_CATEGORY_COLOR

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

  // Escape closes (a date picker that's open closes first); Ctrl/Cmd+Enter saves.
  const formRef = useRef<HTMLFormElement>(null)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault()
        formRef.current?.requestSubmit()
      } else if (e.key === 'Escape' && !document.querySelector('.ctx-backdrop')) {
        onClose()
      }
    }
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
    // A new event's reminders follow: the calendar's defaults when timed, none when all-day.
    if (!isEdit) {
      const cal = calendars.find((c) => c.id === calendarId)
      if (sameReminders(reminders, defaultRemindersFor(cal, allDay))) setReminders(defaultRemindersFor(cal, checked))
    }
    // UNTIL is written differently for all-day series (a date) and timed ones (an instant).
    if (customSpec) applySpec(customSpec, startDate, checked)
  }

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!title.trim()) return
    onSave({
      ...draft,
      calendarId, title: title.trim(), start, end, allDay, categoryId, location, description,
      recurrence: occurrenceOnly ? '' : recurrence,
      reminders, attendees,
    })
  }

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <form ref={formRef} className="modal event-form" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit}>
        <div className="modal-head">
          <span className="eyebrow">
            {onRespond ? 'Invitation' : occurrenceOnly ? 'Edit occurrence' : isEdit ? 'Edit appointment' : 'New appointment'}
          </span>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        {onRespond && (
          <div className="invite-banner">
            <div className="invite-banner-text">
              <span className="invite-from">
                Invitation{draft.organizerEmail ? <> from <strong>{draft.organizerEmail}</strong></> : null}
              </span>
              <span className="invite-current">
                {draft.invitationStatus === 'NeedsAction'
                  ? 'Awaiting your response'
                  : `You responded: ${draft.invitationStatus}`}
              </span>
            </div>
            <div className="invite-actions">
              {(['Accepted', 'Tentative', 'Declined'] as const).map((s) => (
                <button
                  key={s}
                  type="button"
                  className={`invite-btn${draft.invitationStatus === s ? ' is-current' : ''}`}
                  onClick={() => onRespond(s)}
                >
                  {RSVP_LABEL[s]}
                </button>
              ))}
            </div>
          </div>
        )}

        {onScopeChange && draft.scope && (
          <div className="scope-switch" role="radiogroup" aria-label="What to change">
            {(['occurrence', 'series'] as const).map((s) => (
              <button
                key={s}
                type="button"
                role="radio"
                aria-checked={draft.scope === s}
                className={'scope-option' + (draft.scope === s ? ' active' : '')}
                onClick={() => draft.scope !== s && onScopeChange(s)}
              >
                {s === 'occurrence' ? 'This occurrence' : 'All occurrences'}
              </button>
            ))}
          </div>
        )}

        <label htmlFor="ev-title" className="sr-only">Title</label>
        {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
        <input
          id="ev-title"
          className="form-title"
          value={title}
          autoFocus
          placeholder="Add title"
          maxLength={500}
          onChange={(e) => setTitle(e.target.value)}
        />

        <EventWhen
          start={start}
          end={end}
          allDay={allDay}
          onStart={changeStart}
          onEnd={setEnd}
          onAllDay={toggleAllDay}
          startId="ev-start"
          endId="ev-end"
        />

        {occurrenceOnly ? (
          <p className="field-hint form-indent">
            {draft.isOverride
              ? 'This occurrence has its own changes. Saving updates only this one.'
              : 'Saving changes only this occurrence; the rest of the series stays as it is.'}
          </p>
        ) : (
          <div className="form-row form-row-top">
            <FormIcon name="repeat" />
            <div className="form-row-body form-stack">
              <select
                id="ev-repeat"
                aria-label="Repeat"
                className="form-select"
                value={customSpec ? CUSTOM : recurrence}
                onChange={(e) => chooseRepeat(e.target.value)}
              >
                {repeatPresets(startDate).map((r) => (
                  <option key={r.preset} value={r.rule}>
                    {r.label}
                  </option>
                ))}
                {foreignRule && <option value={recurrence}>Custom rule (from another app)</option>}
                <option value={CUSTOM}>{customSpec ? describeRule(recurrence, startDate) : 'Custom…'}</option>
              </select>
              {customSpec && (
                <RecurrenceEditor spec={customSpec} start={startDate} firstDay={firstDay} onChange={(spec) => applySpec(spec)} />
              )}
              {foreignRule && (
                <p className="field-hint">
                  This rule ({recurrence}) was made in another app and can't be edited here. It's kept as
                  it is — pick another option to replace it.
                </p>
              )}
              {isEdit && recurrence !== '' && (
                <p className="field-hint">
                  Saving updates the whole series. Occurrences edited on their own keep their changes,
                  unless the repeat rule or the start changes.
                </p>
              )}
            </div>
          </div>
        )}

        <div className="form-row">
          <ColorDot color={shownColor} />
          <div className="form-row-body form-where">
            {calendars.length > 1 && !occurrenceOnly && (
              <select id="ev-calendar" aria-label="Calendar" className="form-select" value={calendarId} onChange={(e) => chooseCalendar(e.target.value)}>
                {calendars.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            )}
            <CategoryPicker
              value={categoryId}
              onChange={setCategoryId}
              emptyLabel={inherited ? `From calendar · ${inherited.name}` : 'No category'}
              emptyColor={inherited?.color}
              ariaLabel="Category"
            />
          </div>
        </div>

        <div className="form-row form-row-top">
          <FormIcon name="bell" />
          <div className="form-row-body form-stack">
            <div className="form-chips" role="group" aria-label="Reminders">
              {reminders.map((r, i) => (
                <span className="reminder-chip" key={i}>
                  <select
                    value={r.minutesBefore}
                    aria-label="When"
                    onChange={(e) => setReminderOffset(i, Number(e.target.value))}
                  >
                    {!REMINDER_OFFSETS.some((p) => p.value === r.minutesBefore) && (
                      <option value={r.minutesBefore}>{r.minutesBefore} min before</option>
                    )}
                    {REMINDER_OFFSETS.map((p) => (
                      <option key={p.value} value={p.value}>
                        {p.short}
                      </option>
                    ))}
                  </select>
                  <span aria-hidden="true">·</span>
                  <select value={r.channel} onChange={(e) => setReminderChannel(i, e.target.value)} aria-label="Delivery">
                    <option value="Email">Email</option>
                    <option value="WebPush">Browser</option>
                  </select>
                  <button type="button" className="reminder-chip-remove" onClick={() => removeReminder(i)} aria-label="Remove reminder">
                    ✕
                  </button>
                </span>
              ))}
              <button type="button" className="form-link" onClick={addReminder}>
                {reminders.length ? '+ Add' : '+ Add reminder'}
              </button>
            </div>
            {pushHint && <p className="field-hint">{pushHint}</p>}
          </div>
        </div>

        {!occurrenceOnly && (
          <div className="form-row form-row-top">
            <FormIcon name="users" />
            <div className="form-row-body form-stack">
              <div className="guest-chips form-bare">
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
                  aria-label="Guests"
                  value={guestInput}
                  placeholder={attendees.length ? 'Add another…' : 'Add guests'}
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

        <div className="form-row">
          <FormIcon name="pin" />
          <input
            id="ev-location"
            className="form-row-body form-bare-input"
            aria-label="Location"
            value={location}
            placeholder="Add location"
            onChange={(e) => setLocation(e.target.value)}
          />
          {meeting && <JoinLink meeting={meeting} />}
        </div>

        <div className="form-row form-row-top">
          <FormIcon name="text" />
          <textarea
            id="ev-desc"
            className="form-row-body form-textarea"
            aria-label="Description"
            rows={3}
            value={description}
            placeholder="Add description"
            onChange={(e) => setDescription(e.target.value)}
          />
        </div>

        <div className="modal-actions">
          {isEdit && onDelete && (
            <button type="button" className="btn-danger" onClick={onDelete}>
              {occurrenceOnly ? 'Delete occurrence' : draft.scope === 'series' ? 'Delete series' : 'Delete'}
            </button>
          )}
          {isEdit && onCopy && (
            <button type="button" className="btn-ghost" onClick={() => onCopy(draft.id!)}>
              Copy
            </button>
          )}
          <span className="spacer" />
          <span className="form-kbd">Ctrl+Enter</span>
          <button type="button" className="btn-ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="btn-primary" disabled={!title.trim()}>
            Save
          </button>
        </div>
      </form>
    </div>
  )
}
