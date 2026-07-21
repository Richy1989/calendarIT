import { useEffect, useState } from 'react'

export type EventDraft = {
  id?: string
  title: string
  /** 'YYYY-MM-DD' when all-day, otherwise 'YYYY-MM-DDTHH:mm'. */
  start: string
  end: string
  allDay: boolean
  /** Hex color. Default swatches are CSS3 named colors so they sync losslessly via
   *  the iCalendar COLOR property (RFC 7986); custom hex snaps to the nearest name. */
  color: string
}

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
  onSave,
  onDelete,
  onClose,
}: {
  draft: EventDraft
  onSave: (draft: EventDraft) => void
  onDelete?: (id: string) => void
  onClose: () => void
}) {
  const [title, setTitle] = useState(draft.title)
  const [allDay, setAllDay] = useState(draft.allDay)
  const [start, setStart] = useState(draft.start)
  const [end, setEnd] = useState(draft.end)
  const [color, setColor] = useState(draft.color)
  const isEdit = Boolean(draft.id)
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
    onSave({ id: draft.id, title: title.trim(), start, end, allDay, color })
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
