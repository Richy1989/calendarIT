import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import CategoryPicker from './components/CategoryPicker'
import EventWhen from './components/EventWhen'
import { ColorDot, FormIcon } from './components/FormIcons'
import type { EventDraft } from './EventModal'
import { parseLocalValue, toLocalValue } from './lib/dates'
import { defaultRemindersFor, describeReminder, repeatPresets } from './lib/eventForm'
import { describeRule } from './lib/rrule'

type CalendarOption = {
  id: string
  name: string
  defaultCategoryId?: string | null
  defaultReminders?: { minutesBefore: number | string; channel: string }[] | null
}

const WIDTH = 400
const MARGIN = 12

/**
 * Creates an event in place: a small form beside its slot in the grid (a bottom sheet on a
 * phone) with what most new events need — title, time, repeat, calendar and category, guests,
 * location, description. Reminders are the calendar's defaults; everything else is one click
 * away under "More options", which opens the full editor on the same draft.
 *
 * The draft lives in the parent, which draws it in the grid as it is typed (`.fc-draft-ghost`):
 * the form places itself next to that preview and follows it when the time changes.
 */
export default function QuickCreate({
  draft,
  calendars,
  color,
  categoryName,
  mobile,
  onChange,
  onSave,
  onMore,
  onClose,
}: {
  draft: EventDraft
  calendars: CalendarOption[]
  /** The color the event will show in (its category's, else its calendar's default). */
  color: string
  /** The calendar default it inherits, when it has no category of its own: shown as the empty choice. */
  categoryName?: string
  mobile: boolean
  onChange: (draft: EventDraft) => void
  onSave: (draft: EventDraft) => void
  /** Opens the full editor on this draft (`customRepeat`: with the custom repeat editor open). */
  onMore: (draft: EventDraft) => void
  onClose: () => void
}) {
  const formRef = useRef<HTMLFormElement>(null)
  const titleRef = useRef<HTMLInputElement>(null)
  const [repeatOpen, setRepeatOpen] = useState(false)
  const [guestInput, setGuestInput] = useState('')
  const [moved, setMoved] = useState({ x: 0, y: 0 })
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null)

  const set = (patch: Partial<EventDraft>) => onChange({ ...draft, ...patch })
  const startDate = parseLocalValue(draft.start, draft.allDay)
  const calendar = calendars.find((c) => c.id === draft.calendarId) ?? calendars[0]
  const presets = repeatPresets(startDate)
  const repeatLabel = presets.find((p) => p.rule === draft.recurrence)?.label ?? describeRule(draft.recurrence, startDate)
  const canSave = draft.title.trim() !== ''

  // Moving the start drags the end along, keeping the duration.
  const changeStart = (next: string) => {
    const delta = parseLocalValue(next, draft.allDay).getTime() - startDate.getTime()
    const end = toLocalValue(new Date(parseLocalValue(draft.end, draft.allDay).getTime() + delta), draft.allDay)
    set({ start: next, end })
  }

  const toggleAllDay = (allDay: boolean) =>
    set({
      allDay,
      start: allDay ? draft.start.slice(0, 10) : `${draft.start.slice(0, 10)}T09:00`,
      end: allDay ? draft.end.slice(0, 10) : `${draft.end.slice(0, 10)}T10:00`,
      reminders: defaultRemindersFor(calendar, allDay),
    })

  const chooseCalendar = (id: string) => {
    const next = calendars.find((c) => c.id === id)
    set({ calendarId: id, reminders: defaultRemindersFor(next, draft.allDay) })
  }

  const addGuest = () => {
    const email = guestInput.trim().replace(/[,;]$/, '')
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) return // not a mail address (yet): keep typing
    if (!draft.attendees.some((a) => a.email.toLowerCase() === email.toLowerCase())) {
      set({ attendees: [...draft.attendees, { email }] })
    }
    setGuestInput('')
  }

  const save = () => {
    if (!canSave) return
    // A guest typed but not yet committed with Enter still counts.
    const email = guestInput.trim()
    const attendees =
      /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) && !draft.attendees.some((a) => a.email.toLowerCase() === email.toLowerCase())
        ? [...draft.attendees, { email }]
        : draft.attendees
    onSave({ ...draft, title: draft.title.trim(), attendees })
  }
  const saveRef = useRef(save)
  saveRef.current = save

  // The title takes the keyboard. autoFocus alone loses it: the form mounts during the grid's
  // mouse-up, and the grid takes focus back once that event is done.
  useEffect(() => {
    const frame = requestAnimationFrame(() => titleRef.current?.focus())
    return () => cancelAnimationFrame(frame)
  }, [])

  // Keys: Ctrl/Cmd+Enter saves; Escape closes an open menu first, a date picker handles its own.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault()
        saveRef.current()
        return
      }
      if (e.key !== 'Escape' || document.querySelector('.ctx-backdrop')) return
      if (repeatOpen) setRepeatOpen(false)
      else onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [repeatOpen, onClose])

  // A press anywhere else closes the form, as a click outside any popover does. Its own date
  // pickers (portalled to the body) and the preview in the grid don't count as outside.
  useEffect(() => {
    if (mobile) return // the sheet's backdrop handles that
    const onDown = (e: PointerEvent) => {
      const target = e.target as Element | null
      if (!target || formRef.current?.contains(target)) return
      if (target.closest('.ctx-backdrop, .fc-draft-ghost')) return
      onClose()
    }
    window.addEventListener('pointerdown', onDown, true)
    return () => window.removeEventListener('pointerdown', onDown, true)
  }, [mobile, onClose])

  // Beside the preview: to its right, else its left, kept on screen. Recomputed when the event
  // moves, the window resizes or the grid scrolls; the grid draws the preview after this renders,
  // so the first placement waits a frame for it.
  const place = useCallback(() => {
    if (mobile) return
    const form = formRef.current
    const height = form?.offsetHeight ?? 420
    const ghost = document.querySelector('.fc-draft-ghost')
    let left: number
    let top: number
    if (ghost) {
      const r = ghost.getBoundingClientRect()
      left = r.right + MARGIN
      if (left + WIDTH > window.innerWidth - MARGIN) left = r.left - MARGIN - WIDTH
      top = r.top - 40
    } else {
      left = (window.innerWidth - WIDTH) / 2
      top = 96
    }
    left = Math.max(MARGIN, Math.min(left, window.innerWidth - WIDTH - MARGIN))
    top = Math.max(MARGIN, Math.min(top, window.innerHeight - height - MARGIN))
    setPos((p) => (p && Math.abs(p.left - left) < 1 && Math.abs(p.top - top) < 1 ? p : { left, top }))
  }, [mobile])

  useLayoutEffect(() => {
    const frame = requestAnimationFrame(place)
    return () => cancelAnimationFrame(frame)
  }, [place, draft.start, draft.end, draft.allDay, draft.title])

  useEffect(() => {
    window.addEventListener('resize', place)
    window.addEventListener('scroll', place, true)
    return () => {
      window.removeEventListener('resize', place)
      window.removeEventListener('scroll', place, true)
    }
  }, [place])

  // The grip bar moves the form off whatever it covers.
  const startMove = (e: React.PointerEvent) => {
    if (e.button !== 0 || (e.target as Element).closest('button')) return
    e.preventDefault()
    const origin = { x: e.clientX - moved.x, y: e.clientY - moved.y }
    const onMove = (ev: PointerEvent) => setMoved({ x: ev.clientX - origin.x, y: ev.clientY - origin.y })
    const onUp = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
  }

  const form = (
    <form
      ref={formRef}
      className={'quick-create' + (mobile ? ' is-sheet' : '')}
      aria-label="New appointment"
      style={
        mobile
          ? undefined
          : {
              left: pos?.left ?? -9999,
              top: pos?.top ?? 0,
              transform: `translate(${moved.x}px, ${moved.y}px)`,
              // Not visibility:hidden while it's being placed: a hidden field can't take focus.
              opacity: pos ? 1 : 0,
            }
      }
      onSubmit={(e) => {
        e.preventDefault()
        save()
      }}
    >
      {mobile ? (
        <div className="qc-sheet-head">
          <span className="eyebrow">New appointment</span>
          <button type="button" className="qc-close" onClick={onClose} aria-label="Close">
            <FormIcon name="close" />
          </button>
        </div>
      ) : (
        <div className="qc-grip" onPointerDown={startMove}>
          <FormIcon name="grip" />
          <button type="button" className="qc-close" onClick={onClose} aria-label="Close">
            <FormIcon name="close" />
          </button>
        </div>
      )}

      <div className="qc-body">
        <label htmlFor="qc-title" className="sr-only">Title</label>
        {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
        <input
          id="qc-title"
          ref={titleRef}
          className="form-title"
          value={draft.title}
          autoFocus
          placeholder="Add title"
          maxLength={500}
          onChange={(e) => set({ title: e.target.value })}
        />

        <EventWhen
          start={draft.start}
          end={draft.end}
          allDay={draft.allDay}
          onStart={changeStart}
          onEnd={(end) => set({ end })}
          onAllDay={toggleAllDay}
        />

        <div className="form-row qc-repeat">
          <FormIcon name="repeat" />
          <div className="form-row-body">
            <button
              type="button"
              className={'form-menu-btn' + (draft.recurrence ? '' : ' is-quiet')}
              aria-haspopup="listbox"
              aria-expanded={repeatOpen}
              onClick={() => setRepeatOpen((o) => !o)}
            >
              {repeatLabel}
              <FormIcon name="chevron" className="form-icon-sm" />
            </button>
            {repeatOpen && (
              <div className="form-menu" role="listbox" aria-label="Repeat">
                {presets.map((p) => (
                  <button
                    key={p.preset}
                    type="button"
                    role="option"
                    aria-selected={p.rule === draft.recurrence}
                    className={'form-menu-item' + (p.rule === draft.recurrence ? ' is-current' : '')}
                    onClick={() => {
                      set({ recurrence: p.rule })
                      setRepeatOpen(false)
                    }}
                  >
                    {p.label}
                  </button>
                ))}
                <div className="form-menu-sep" />
                <button type="button" className="form-menu-item" onClick={() => onMore({ ...draft, customRepeat: true })}>
                  Custom…
                </button>
              </div>
            )}
          </div>
        </div>

        <div className="form-row">
          <ColorDot color={color} />
          <div className="form-row-body form-where">
            {calendars.length > 1 && (
              <select aria-label="Calendar" className="form-select" value={calendar?.id} onChange={(e) => chooseCalendar(e.target.value)}>
                {calendars.map((c) => (
                  <option key={c.id} value={c.id}>{c.name}</option>
                ))}
              </select>
            )}
            <CategoryPicker
              value={draft.categoryId}
              onChange={(categoryId) => set({ categoryId })}
              emptyLabel={categoryName ? `From calendar · ${categoryName}` : 'No category'}
              ariaLabel="Category"
            />
          </div>
        </div>

        <div className="form-row">
          <FormIcon name="users" />
          <div className="form-row-body guest-chips form-bare">
            {draft.attendees.map((a) => (
              <span className="guest-chip" key={a.email}>
                {a.email}
                <button
                  type="button"
                  className="guest-chip-remove"
                  onClick={() => set({ attendees: draft.attendees.filter((x) => x.email !== a.email) })}
                  aria-label={`Remove ${a.email}`}
                >
                  ✕
                </button>
              </span>
            ))}
            <input
              aria-label="Guests"
              value={guestInput}
              placeholder={draft.attendees.length ? 'Add another…' : 'Add guests'}
              onChange={(e) => setGuestInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ',') {
                  e.preventDefault()
                  addGuest()
                }
              }}
              onBlur={addGuest}
            />
          </div>
        </div>

        <div className="form-row">
          <FormIcon name="pin" />
          <input
            className="form-row-body form-bare-input"
            aria-label="Location"
            placeholder="Add location"
            value={draft.location}
            onChange={(e) => set({ location: e.target.value })}
          />
        </div>

        <div className="form-row">
          <FormIcon name="text" />
          <input
            className="form-row-body form-bare-input"
            aria-label="Description"
            placeholder="Add description"
            value={draft.description}
            onChange={(e) => set({ description: e.target.value })}
          />
        </div>

        <div className="form-row form-row-quiet">
          <FormIcon name="bell" />
          <span className="form-row-body form-note">
            {draft.reminders.length === 0
              ? 'No reminders'
              : `${draft.reminders.map(describeReminder).join(', ')}${calendar ? ` · from the ${calendar.name} calendar` : ''}`}
          </span>
        </div>

        <div className="qc-actions">
          {!mobile && <span className="form-kbd">Ctrl+Enter</span>}
          <span className="spacer" />
          <button type="button" className="btn-ghost" onClick={() => onMore(draft)}>
            More options
          </button>
          <button type="submit" className="btn-primary" disabled={!canSave}>
            Save
          </button>
        </div>
      </div>
    </form>
  )

  return createPortal(
    mobile ? (
      <div className="qc-sheet-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
        {form}
      </div>
    ) : (
      form
    ),
    document.body,
  )
}
