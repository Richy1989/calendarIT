import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { searchEvents, type EventSearchResult } from './api/events'
import { useHour12 } from './clock'

function formatWhen(iso: string, allDay: boolean, hour12: boolean): string {
  const d = new Date(iso)
  const date = d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })
  if (allDay) return `${date} · All day`
  const time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hour12 })
  return `${date} · ${time}`
}

const DEFAULT_COLOR = '#7B68EE'

/**
 * Header search box. Debounces input, queries the backend for matching appointments, and
 * shows them in a dropdown. Picking one hands its date up via `onPick` (App navigates the
 * calendar to that day). Keyboard: ↑/↓ to move, Enter to pick, Esc to close.
 */
export default function SearchBar({ onPick }: { onPick: (isoDate: string) => void }) {
  const hour12 = useHour12()
  const [q, setQ] = useState('')
  const [debounced, setDebounced] = useState('')
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  // Type-to-search: a printable keystroke anywhere on the page (not inside a field, and
  // with no dialog/popover open) focuses the search box, and that same keystroke lands
  // in it — focusing during keydown makes the browser deliver the character here.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key.length !== 1 || e.metaKey || e.ctrlKey || e.altKey) return // printable chars only, no shortcuts
      if (e.key === ' ') return // space scrolls / activates buttons — not a search trigger
      const target = e.target as HTMLElement | null
      const tag = target?.tagName
      if (target?.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return
      if (document.querySelector('.modal-overlay, .ctx-backdrop')) return // the editor or a popover owns the keyboard
      const el = inputRef.current
      if (el && document.activeElement !== el) el.focus()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Debounce so we don't fire a request on every keystroke.
  useEffect(() => {
    const id = setTimeout(() => setDebounced(q.trim()), 180)
    return () => clearTimeout(id)
  }, [q])

  const { data: results = [] } = useQuery({
    queryKey: ['event-search', debounced],
    queryFn: () => searchEvents(debounced),
    enabled: debounced.length > 0,
    staleTime: 10_000,
  })

  // Reset the highlight whenever the result set changes.
  useEffect(() => setActive(0), [results])

  // Keep the keyboard-highlighted hit visible when the list is long enough to scroll.
  useEffect(() => {
    document.getElementById(`search-hit-${active}`)?.scrollIntoView({ block: 'nearest' })
  }, [active])

  // Close the dropdown on any click outside the search box.
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [open])

  const pick = (r: EventSearchResult) => {
    onPick(r.start)
    setOpen(false)
    setQ('')
    setDebounced('')
  }

  const showDropdown = open && debounced.length > 0 && results.length > 0
  const showEmpty = open && debounced.length > 0 && results.length === 0

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') {
      // First Esc closes the dropdown; the next one clears and leaves the box, so the
      // arrow keys page the calendar again.
      if (open && debounced.length > 0) {
        setOpen(false)
      } else {
        setQ('')
        setDebounced('')
        inputRef.current?.blur()
      }
      return
    }
    if (!results.length) return
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setActive((i) => (i + 1) % results.length)
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((i) => (i - 1 + results.length) % results.length)
    } else if (e.key === 'Enter') {
      e.preventDefault()
      pick(results[active] ?? results[0])
    }
  }

  return (
    <div className="search" ref={rootRef}>
      <svg className="search-icon" width="16" height="16" viewBox="0 0 24 24" fill="none" aria-hidden="true">
        <circle cx="11" cy="11" r="7" stroke="currentColor" strokeWidth="2" />
        <path d="m20 20-3.5-3.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
      </svg>
      <input
        ref={inputRef}
        type="text"
        className="search-input"
        placeholder="Search appointments…"
        value={q}
        onChange={(e) => {
          setQ(e.target.value)
          setOpen(true)
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown}
        role="combobox"
        aria-expanded={showDropdown}
        aria-controls="search-results"
        aria-autocomplete="list"
        aria-activedescendant={showDropdown ? `search-hit-${active}` : undefined}
      />

      {showDropdown && (
        <ul className="search-results" id="search-results" role="listbox">
          {results.map((r, i) => (
            <li
              key={`${r.id}-${r.start}`}
              id={`search-hit-${i}`}
              role="option"
              aria-selected={i === active}
              className={`search-hit${i === active ? ' is-active' : ''}`}
              onMouseEnter={() => setActive(i)}
              // onMouseDown (not onClick) so it fires before the input's blur closes us.
              onMouseDown={(e) => {
                e.preventDefault()
                pick(r)
              }}
            >
              <span className="search-hit-dot" style={{ background: r.color ?? DEFAULT_COLOR }} />
              <span className="search-hit-text">
                <span className="search-hit-title">{r.title}</span>
                <span className="search-hit-when">
                  {formatWhen(r.start, r.allDay, hour12)}
                  {r.location ? ` · ${r.location}` : ''}
                  {r.recurring ? ' · repeats' : ''}
                </span>
              </span>
            </li>
          ))}
        </ul>
      )}

      {showEmpty && (
        <div className="search-results search-empty">No appointments found</div>
      )}
    </div>
  )
}
