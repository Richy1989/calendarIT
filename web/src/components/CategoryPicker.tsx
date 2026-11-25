import { useRef, useState, type KeyboardEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createCategory, listCategories } from '../api/categories'
import { nextCategoryColor } from '../lib/categoryColors'
import { anchorBelow, Popover, type Anchor } from './Popover'
import { FormIcon } from './FormIcons'

/** A category's color; dashed and empty for "none". */
function Dot({ color }: { color?: string }) {
  return <span className={'cat-dot' + (color ? '' : ' cat-dot-none')} style={color ? { background: color } : undefined} aria-hidden="true" />
}

/**
 * Picks a category — or creates one on the spot ("＋ New category…"), so choosing a calendar's
 * category never means a detour through the Categories page first. Creating one selects it.
 * A button and a listbox rather than a native <select>, so every choice shows its color.
 */
export default function CategoryPicker({
  value,
  onChange,
  suggestedName,
  emptyLabel = 'No category',
  emptyColor,
  id,
  ariaLabel,
  disabled,
}: {
  value: string | null
  onChange: (categoryId: string | null) => void
  /** Pre-fills a new category's name (e.g. the calendar's name: "Holidays"). */
  suggestedName?: string
  emptyLabel?: string
  /** The empty choice's color, when it stands for an inherited category ("From calendar · Work"). */
  emptyColor?: string
  id?: string
  ariaLabel?: string
  disabled?: boolean
}) {
  const queryClient = useQueryClient()
  const { data: categories = [] } = useQuery({ queryKey: ['categories'], queryFn: listCategories })
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [color, setColor] = useState<string | null>(null)
  const [menu, setMenu] = useState<Anchor | null>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  const create = useMutation({
    mutationFn: () => createCategory(name.trim(), color ?? nextCategoryColor(categories.length)),
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ['categories'] })
      setCreating(false)
      onChange(created.id)
    },
  })

  const selected = categories.find((c) => c.id === value)
  const shownColor = color ?? nextCategoryColor(categories.length)

  const startCreating = () => {
    setName(suggestedName?.trim() ?? '')
    setColor(null)
    create.reset()
    setCreating(true)
  }

  const closeMenu = () => {
    setMenu(null)
    buttonRef.current?.focus()
  }
  const choose = (categoryId: string | null) => {
    setCreating(false)
    onChange(categoryId)
    closeMenu()
  }
  const openMenu = () => {
    if (buttonRef.current) setMenu(anchorBelow(buttonRef.current, 4))
    // Focus the current choice once the list is on screen, so arrows and Enter work at once.
    requestAnimationFrame(() =>
      (listRef.current?.querySelector<HTMLElement>('[aria-selected="true"]') ??
        listRef.current?.querySelector<HTMLElement>('[role="option"]'))?.focus(),
    )
  }
  const onListKey = (e: KeyboardEvent) => {
    const items = [...(listRef.current?.querySelectorAll<HTMLElement>('[role="option"]') ?? [])]
    const at = items.indexOf(document.activeElement as HTMLElement)
    const go = (i: number) => items[Math.max(0, Math.min(items.length - 1, i))]?.focus()
    if (e.key === 'ArrowDown') go(at + 1)
    else if (e.key === 'ArrowUp') go(at - 1)
    else if (e.key === 'Home') go(0)
    else if (e.key === 'End') go(items.length - 1)
    else if (e.key === 'Tab') closeMenu()
    else return
    e.preventDefault()
  }

  return (
    <div className="category-picker">
      <button
        ref={buttonRef}
        type="button"
        id={id}
        className="category-picker-btn"
        aria-label={ariaLabel}
        aria-haspopup="listbox"
        aria-expanded={!!menu}
        disabled={disabled}
        onClick={() => (menu ? closeMenu() : openMenu())}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault()
            openMenu()
          }
        }}
      >
        <Dot color={creating ? shownColor : selected ? selected.color : emptyColor} />
        <span className="category-picker-label">{creating ? 'New category' : (selected?.name ?? emptyLabel)}</span>
        <FormIcon name="chevron" className="form-icon-sm" />
      </button>

      {menu && (
        <Popover anchor={menu} onClose={closeMenu} className="cal-switcher-menu category-menu">
          <div ref={listRef} role="listbox" aria-label={ariaLabel ?? 'Category'} onKeyDown={onListKey}>
            <button
              type="button"
              role="option"
              aria-selected={!creating && !selected}
              className="cal-switcher-item"
              onClick={() => choose(null)}
            >
              <Dot color={emptyColor} />
              <span className="category-menu-name">{emptyLabel}</span>
            </button>
            {categories.map((c) => (
              <button
                key={c.id}
                type="button"
                role="option"
                aria-selected={!creating && c.id === value}
                className="cal-switcher-item"
                onClick={() => choose(c.id)}
              >
                <Dot color={c.color} />
                <span className="category-menu-name">{c.name}</span>
              </button>
            ))}
            <div className="form-menu-sep" />
            <button
              type="button"
              role="option"
              aria-selected={creating}
              className="cal-switcher-item cal-switcher-manage"
              onClick={() => {
                setMenu(null)
                startCreating()
              }}
            >
              ＋ New category…
            </button>
          </div>
        </Popover>
      )}

      {creating && (
        <span className="category-picker-new">
          <label className="category-dot-pick" style={{ ['--sw']: shownColor } as React.CSSProperties} title="Pick color">
            <input type="color" value={shownColor} onChange={(e) => setColor(e.target.value)} />
          </label>
          {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
          <input
            value={name}
            autoFocus
            maxLength={100}
            placeholder="Category name"
            aria-label="New category name"
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault() // inside a form, Enter must create the category, not submit the form
                if (name.trim()) create.mutate()
              }
              if (e.key === 'Escape') {
                e.stopPropagation()
                setCreating(false)
              }
            }}
          />
          <button type="button" className="btn-primary" disabled={!name.trim() || create.isPending} onClick={() => create.mutate()}>
            Add
          </button>
          <button type="button" className="btn-ghost" onClick={() => setCreating(false)}>
            Cancel
          </button>
          {create.isError && <span className="error">{(create.error as Error).message}</span>}
        </span>
      )}
    </div>
  )
}
