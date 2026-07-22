import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { searchEvents, type EventSearchResult } from './api/events'

function formatWhen(iso: string, allDay: boolean): string {
  const d = new Date(iso)
  const date = d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })
  if (allDay) return `${date} · All day`
  const time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  return `${date} · ${time}`
}

const DEFAULT_COLOR = '#7B68EE'

/**
 * Header search box. Debounces input, queries the backend for matching appointments, and
 * shows them in a dropdown. Picking one hands its date up via `onPick` (App navigates the
 * calendar to that day). Keyboard: ↑/↓ to move, Enter to pick, Esc to close.
 */
export default function SearchBar({ onPick }: { onPick: (isoDate: string) => void }) {
  const [q, setQ] = useState('')
  const [debounced, setDebounced] = useState('')
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)

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
      setOpen(false)
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
      />

      {showDropdown && (
        <ul className="search-results" id="search-results" role="listbox">
          {results.map((r, i) => (
            <li
              key={`${r.id}-${r.start}`}
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
                  {formatWhen(r.start, r.allDay)}
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
