import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createCategory, listCategories } from '../api/categories'
import { nextCategoryColor } from '../lib/categoryColors'

/** The select's "create one" entry; never a category id. */
const NEW = '__new__'

/**
 * Picks a category — or creates one on the spot ("＋ New category…"), so choosing a calendar's
 * category never means a detour through the Categories page first. Creating one selects it.
 */
export default function CategoryPicker({
  value,
  onChange,
  suggestedName,
  emptyLabel = 'No category',
  id,
  ariaLabel,
  disabled,
}: {
  value: string | null
  onChange: (categoryId: string | null) => void
  /** Pre-fills a new category's name (e.g. the calendar's name: "Holidays"). */
  suggestedName?: string
  emptyLabel?: string
  id?: string
  ariaLabel?: string
  disabled?: boolean
}) {
  const queryClient = useQueryClient()
  const { data: categories = [] } = useQuery({ queryKey: ['categories'], queryFn: listCategories })
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [color, setColor] = useState<string | null>(null)

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

  return (
    <div className="category-picker">
      <span className="category-picker-row">
        <span
          className={'cat-dot' + (selected ? '' : ' cat-dot-none')}
          style={selected ? { background: selected.color } : undefined}
          aria-hidden="true"
        />
        <select
          id={id}
          aria-label={ariaLabel}
          disabled={disabled}
          value={creating ? NEW : (value ?? '')}
          onChange={(e) => (e.target.value === NEW ? startCreating() : onChange(e.target.value || null))}
        >
          <option value="">{emptyLabel}</option>
          {categories.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
          <option value={NEW}>＋ New category…</option>
        </select>
      </span>

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
