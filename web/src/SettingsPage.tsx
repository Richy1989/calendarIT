import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { deleteAvatar, getProfile, uploadAvatar } from './api/profile'
import { exportIcs, importIcs } from './api/events'
import { createCalendar, deleteCalendar, listCalendars, renameCalendar, type CalendarDto } from './api/calendars'
import { createCategory, deleteCategory, listCategories, updateCategory, type CategoryDto } from './api/categories'
import { deleteMailAccount, getMailAccount, saveMailAccount, testMailAccount } from './api/mailAccount'
import Logo from './Logo'

type Section = 'general' | 'calendars' | 'categories' | 'sync' | 'security' | 'email'

/** Dedicated, full-page settings screen: sidebar nav + a content card per section. */
export default function SettingsPage({
  onBack,
  onLogout,
  initialSection,
}: {
  onBack: () => void
  onLogout: () => void
  initialSection?: Section
}) {
  const [section, setSection] = useState<Section>(initialSection ?? 'general')

  return (
    <div className="settings">
      <header className="settings-header">
        <button className="settings-back" onClick={onBack} aria-label="Back to calendar">
          <ChevronLeft />
        </button>
        <Logo />
        <h1>Settings</h1>
      </header>

      <div className="settings-body">
        <nav className="settings-nav">
          <button className={navClass(section === 'general')} onClick={() => setSection('general')}>
            <UserIcon /> General
          </button>
          <button className={navClass(section === 'calendars')} onClick={() => setSection('calendars')}>
            <CalendarIcon /> Calendars
          </button>
          <button className={navClass(section === 'categories')} onClick={() => setSection('categories')}>
            <TagIcon /> Categories
          </button>
          <button className={navClass(section === 'sync')} onClick={() => setSection('sync')}>
            <PhoneIcon /> Sync
          </button>
          <button className={navClass(section === 'security')} onClick={() => setSection('security')}>
            <ShieldIcon /> Security
          </button>
          <button className={navClass(section === 'email')} onClick={() => setSection('email')}>
            <MailIcon /> Email
          </button>
          <div className="settings-nav-divider" />
          <button className="settings-nav-item settings-signout" onClick={onLogout}>
            <SignOutIcon /> Sign out
          </button>
        </nav>

        <div className="settings-content">
          {section === 'general' ? (
            <GeneralSection />
          ) : section === 'calendars' ? (
            <CalendarsSection />
          ) : section === 'categories' ? (
            <CategoriesSection />
          ) : section === 'sync' ? (
            <SyncSection />
          ) : section === 'email' ? (
            <EmailSection />
          ) : (
            <div className="settings-card settings-placeholder">
              <h2>Security</h2>
              <p className="settings-sub">Coming soon.</p>
            </div>
          )}
        </div>
      </div>

      <footer className="settings-foot">CalendarIT · self-hosted</footer>
    </div>
  )
}

function GeneralSection() {
  const queryClient = useQueryClient()
  const { data: profile } = useQuery({ queryKey: ['profile'], queryFn: getProfile })
  const [file, setFile] = useState<File | null>(null)
  const [error, setError] = useState<string | null>(null)

  const localPreview = useMemo(() => (file ? URL.createObjectURL(file) : null), [file])
  const shownAvatar = localPreview ?? profile?.avatarDataUrl ?? null
  const initial = (profile?.email?.trim()?.[0] ?? '?').toUpperCase()

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['profile'] })
  const uploadMut = useMutation({
    mutationFn: uploadAvatar,
    onSuccess: () => { setFile(null); refresh() },
    onError: (e) => setError((e as Error).message),
  })
  const removeMut = useMutation({ mutationFn: deleteAvatar, onSuccess: () => { setFile(null); refresh() } })

  const [dataNotice, setDataNotice] = useState<string | null>(null)
  const { data: calendars = [] } = useQuery({ queryKey: ['calendars'], queryFn: listCalendars })

  // Export: with several calendars, pick which ones go into the file first.
  const [exportSel, setExportSel] = useState<string[] | null>(null) // null = picker closed
  // Import: the picked file waits here until a target calendar is chosen.
  const [pendingImport, setPendingImport] = useState<{ name: string; ics: string } | null>(null)
  const [importTarget, setImportTarget] = useState<string>('') // calendar id, or 'new'
  const [importNewName, setImportNewName] = useState('')

  const download = (blob: Blob) => {
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'calendarit.ics'
    a.click()
    URL.revokeObjectURL(url)
  }

  const handleExport = async () => {
    setDataNotice(null)
    if (calendars.length > 1) {
      setPendingImport(null)
      setExportSel(calendars.map((c) => c.id)) // open the picker with everything ticked
      return
    }
    try {
      download(await exportIcs())
    } catch {
      setDataNotice('Export failed.')
    }
  }

  const doExport = async () => {
    if (!exportSel?.length) return
    try {
      // Sending no filter when everything is selected keeps the URL clean.
      download(await exportIcs(exportSel.length === calendars.length ? undefined : exportSel))
      setExportSel(null)
    } catch {
      setDataNotice('Export failed.')
    }
  }

  const handleImport = () => {
    setDataNotice(null)
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.ics,text/calendar'
    input.onchange = async () => {
      const chosen = input.files?.[0]
      if (!chosen) return
      setExportSel(null)
      setPendingImport({ name: chosen.name, ics: await chosen.text() })
      setImportTarget(calendars[0]?.id ?? 'new')
      setImportNewName(chosen.name.replace(/\.ics$/i, ''))
    }
    input.click()
  }

  const doImport = async () => {
    if (!pendingImport) return
    try {
      const target =
        importTarget === 'new' ? { newCalendarName: importNewName.trim() || 'Imported' } : { calendarId: importTarget }
      const result = await importIcs(pendingImport.ics, target)
      await queryClient.invalidateQueries({ queryKey: ['events'] })
      await queryClient.invalidateQueries({ queryKey: ['calendars'] })
      setPendingImport(null)
      setDataNotice(`Imported ${result.imported}, skipped ${result.skipped}.`)
    } catch {
      setDataNotice('Import failed — is it a valid .ics file?')
    }
  }

  const pick = () => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = 'image/png,image/jpeg,image/webp,image/gif'
    input.onchange = () => {
      const chosen = input.files?.[0]
      if (chosen) { setError(null); setFile(chosen) }
    }
    input.click()
  }

  return (
    <>
    <div className="settings-card">
      <h2>Profile picture</h2>
      <p className="settings-sub">Add a user icon shown next to your account.</p>

      <div className="avatar-edit">
        <div className="avatar-with-badge">
          {shownAvatar ? (
            <img className="avatar-preview" src={shownAvatar} alt="" />
          ) : (
            <span className="avatar-preview avatar-fallback">{initial}</span>
          )}
          <button type="button" className="avatar-cam" onClick={pick} aria-label="Choose image">
            <CameraIcon />
          </button>
        </div>

        <div className="avatar-side">
          <div className="avatar-actions">
            <button type="button" className="link-btn" onClick={pick}>
              Choose image
            </button>
            <button
              type="button"
              className="btn-primary"
              onClick={() => file && uploadMut.mutate(file)}
              disabled={!file || uploadMut.isPending}
            >
              {uploadMut.isPending ? 'Saving…' : 'Save'}
            </button>
            {profile?.avatarDataUrl && !file && (
              <button type="button" className="btn-ghost" onClick={() => removeMut.mutate()} disabled={removeMut.isPending}>
                Remove
              </button>
            )}
          </div>
          <p className="field-hint">PNG, JPEG, WEBP, or GIF — up to 3 MB.</p>
          {error && <p className="error">{error}</p>}
        </div>
      </div>

      <div className="settings-divider" />

      <div className="settings-rows">
        <div className="settings-row">
          <span className="settings-row-label">Email</span>
          <span className="settings-row-value">{profile?.email}</span>
        </div>
      </div>
    </div>

    <div className="settings-card">
      <h2>Import &amp; export</h2>
      <p className="settings-sub">Move your calendar in and out as standard iCalendar (.ics).</p>
      <div className="data-actions">
        <button type="button" className="btn-ghost" onClick={handleExport}>
          Export .ics
        </button>
        <button type="button" className="btn-ghost" onClick={handleImport}>
          Import .ics
        </button>
      </div>

      {exportSel && (
        <div className="io-panel">
          <span className="io-panel-label">Which calendars go into the file?</span>
          <div className="io-cals">
            {calendars.map((c) => {
              const on = exportSel.includes(c.id)
              return (
                <button
                  key={c.id}
                  type="button"
                  className="io-cal-toggle"
                  onClick={() =>
                    setExportSel(on ? exportSel.filter((id) => id !== c.id) : [...exportSel, c.id])
                  }
                >
                  <span className={'cal-check' + (on ? ' on' : '')} />
                  {c.name}
                </button>
              )
            })}
          </div>
          <div className="io-actions">
            <button type="button" className="btn-primary" onClick={doExport} disabled={exportSel.length === 0}>
              Download .ics
            </button>
            <button type="button" className="btn-ghost" onClick={() => setExportSel(null)}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {pendingImport && (
        <div className="io-panel">
          <span className="io-panel-label">Import “{pendingImport.name}” into</span>
          <div className="io-import-grid">
            <div className="field">
              <label htmlFor="io-target">Calendar</label>
              <select id="io-target" value={importTarget} onChange={(e) => setImportTarget(e.target.value)}>
                {calendars.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
                <option value="new">New calendar…</option>
              </select>
            </div>
            {importTarget === 'new' && (
              <div className="field">
                <label htmlFor="io-new-name">Name</label>
                <input
                  id="io-new-name"
                  value={importNewName}
                  maxLength={200}
                  placeholder="Calendar name"
                  onChange={(e) => setImportNewName(e.target.value)}
                />
              </div>
            )}
          </div>
          <div className="io-actions">
            <button
              type="button"
              className="btn-primary"
              onClick={doImport}
              disabled={importTarget === 'new' && !importNewName.trim()}
            >
              Import
            </button>
            <button type="button" className="btn-ghost" onClick={() => setPendingImport(null)}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {dataNotice && <p className="field-hint">{dataNotice}</p>}
    </div>
    </>
  )
}

/** App-styled replacement for window.confirm, matching the event modal's look. */
function ConfirmDialog({
  title,
  message,
  confirmLabel = 'Delete',
  onConfirm,
  onClose,
}: {
  title: string
  message: string
  confirmLabel?: string
  onConfirm: () => void
  onClose: () => void
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div className="modal" role="alertdialog" aria-modal="true" aria-label={title} onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <span className="eyebrow">{title}</span>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <p className="confirm-text">{message}</p>
        <div className="modal-actions">
          <span className="spacer" />
          <button type="button" className="btn-ghost" onClick={onClose}>
            Cancel
          </button>
          {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
          <button type="button" className="btn-danger" autoFocus onClick={onConfirm}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}

/** Manage the user's calendars: rename inline, create new ones, delete (with their events). */
function CalendarsSection() {
  const queryClient = useQueryClient()
  const { data: calendars = [] } = useQuery({ queryKey: ['calendars'], queryFn: listCalendars })
  const [adding, setAdding] = useState(false)
  const [newName, setNewName] = useState('')
  const [notice, setNotice] = useState<string | null>(null)

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['calendars'] })
    queryClient.invalidateQueries({ queryKey: ['events'] })
  }
  const createMut = useMutation({
    mutationFn: (name: string) => createCalendar(name),
    onSuccess: () => { setNewName(''); setAdding(false); setNotice(null); refresh() },
    onError: (e) => setNotice((e as Error).message),
  })
  const renameMut = useMutation({
    mutationFn: (v: { id: string; name: string }) => renameCalendar(v.id, v.name),
    onSuccess: () => { setNotice(null); refresh() },
    onError: (e) => setNotice((e as Error).message),
  })
  const deleteMut = useMutation({
    mutationFn: (id: string) => deleteCalendar(id),
    onSuccess: () => { setNotice(null); refresh() },
    onError: (e) => setNotice((e as Error).message),
  })

  const [confirmDelete, setConfirmDelete] = useState<CalendarDto | null>(null)
  const confirmMessage = (c: CalendarDto) => {
    const events = c.eventCount === 1 ? 'its 1 appointment' : `its ${c.eventCount} appointments`
    return `Delete "${c.name}" and ${events}? This cannot be undone.`
  }

  return (
    <div className="settings-card">
      <h2>Calendars</h2>
      <p className="settings-sub">
        Split your schedule into separate calendars — say, Personal and Work — and toggle them from the
        heading above the calendar. Each one syncs as its own calendar over CalDAV.
      </p>

      <ul className="cal-list">
        {calendars.map((c) => (
          <CalendarRow
            key={c.id}
            calendar={c}
            onRename={(name) => renameMut.mutate({ id: c.id, name })}
            onDelete={() => setConfirmDelete(c)}
            canDelete={calendars.length > 1}
          />
        ))}
      </ul>

      {confirmDelete && (
        <ConfirmDialog
          title="Delete calendar"
          message={confirmMessage(confirmDelete)}
          confirmLabel="Delete calendar"
          onConfirm={() => {
            deleteMut.mutate(confirmDelete.id)
            setConfirmDelete(null)
          }}
          onClose={() => setConfirmDelete(null)}
        />
      )}

      {adding ? (
        <form
          className="cal-add-row"
          onSubmit={(e) => {
            e.preventDefault()
            if (newName.trim()) createMut.mutate(newName.trim())
          }}
        >
          <span className="cal-tile cal-tile-new" aria-hidden="true">
            {(newName.trim()[0] ?? '+').toUpperCase()}
          </span>
          {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
          <input
            value={newName}
            autoFocus
            placeholder="Calendar name"
            maxLength={200}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                setAdding(false)
                setNewName('')
              }
            }}
          />
          <button type="submit" className="btn-primary" disabled={!newName.trim() || createMut.isPending}>
            Add
          </button>
          <button type="button" className="btn-ghost" onClick={() => { setAdding(false); setNewName('') }}>
            Cancel
          </button>
        </form>
      ) : (
        <button type="button" className="cal-add" onClick={() => setAdding(true)}>
          ＋ New calendar
        </button>
      )}

      {notice && <p className="error">{notice}</p>}
    </div>
  )
}

/** One calendar in the list: gradient initial tile, name (click ✎ to rename), count, quiet delete. */
function CalendarRow({
  calendar,
  onRename,
  onDelete,
  canDelete,
}: {
  calendar: CalendarDto
  onRename: (name: string) => void
  onDelete: () => void
  canDelete: boolean
}) {
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(calendar.name)

  const commit = () => {
    setEditing(false)
    const trimmed = name.trim()
    if (!trimmed || trimmed === calendar.name) {
      setName(calendar.name) // nothing to save (and never blank a name)
    } else {
      onRename(trimmed)
    }
  }

  const initial = (calendar.name.trim()[0] ?? '?').toUpperCase()
  const count = calendar.eventCount === 1 ? '1 appointment' : `${calendar.eventCount} appointments`

  return (
    <li className="cal-item">
      <span className="cal-tile" aria-hidden="true">{initial}</span>

      <span className="cal-item-body">
        {editing ? (
          // eslint-disable-next-line jsx-a11y/no-autofocus
          <input
            className="cal-item-rename"
            value={name}
            autoFocus
            maxLength={200}
            aria-label={`Rename ${calendar.name}`}
            onChange={(e) => setName(e.target.value)}
            onBlur={commit}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commit()
              if (e.key === 'Escape') {
                setName(calendar.name)
                setEditing(false)
              }
            }}
          />
        ) : (
          <span className="cal-item-name">{calendar.name}</span>
        )}
        <span className="cal-item-meta">{count}</span>
      </span>

      <span className="cal-item-actions">
        {!editing && (
          <button type="button" className="cal-item-action" onClick={() => setEditing(true)} aria-label={`Rename ${calendar.name}`} title="Rename">
            ✎
          </button>
        )}
        <button
          type="button"
          className="cal-item-action danger"
          onClick={onDelete}
          disabled={!canDelete}
          aria-label={`Delete ${calendar.name}`}
          title={canDelete ? 'Delete' : "You can't delete your last calendar."}
        >
          ✕
        </button>
      </span>
    </li>
  )
}

// New categories cycle through these starter colors (exact CSS3-named hexes, so they
// round-trip losslessly through the iCalendar COLOR property).
const CATEGORY_COLORS = ['#7B68EE', '#6495ED', '#40E0D0', '#3CB371', '#DAA520', '#DB7093', '#FF6347', '#708090']

/** Manage the user's categories: the named colors appointments take their color from. */
function CategoriesSection() {
  const queryClient = useQueryClient()
  const { data: categories = [] } = useQuery({ queryKey: ['categories'], queryFn: listCategories })
  const [adding, setAdding] = useState(false)
  const [newName, setNewName] = useState('')
  const [newColor, setNewColor] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const nextColor = () => CATEGORY_COLORS[categories.length % CATEGORY_COLORS.length]

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['categories'] })
    queryClient.invalidateQueries({ queryKey: ['events'] })
  }
  const createMut = useMutation({
    mutationFn: (v: { name: string; color: string }) => createCategory(v.name, v.color),
    onSuccess: () => { setNewName(''); setNewColor(null); setAdding(false); setNotice(null); refresh() },
    onError: (e) => setNotice((e as Error).message),
  })
  const updateMut = useMutation({
    mutationFn: (v: { id: string; name: string; color: string }) => updateCategory(v.id, v.name, v.color),
    onSuccess: () => { setNotice(null); refresh() },
    onError: (e) => setNotice((e as Error).message),
  })
  const deleteMut = useMutation({
    mutationFn: (id: string) => deleteCategory(id),
    onSuccess: () => { setNotice(null); refresh() },
    onError: (e) => setNotice((e as Error).message),
  })

  const [confirmDelete, setConfirmDelete] = useState<CategoryDto | null>(null)
  const confirmMessage = (c: CategoryDto) => {
    const count = Number(c.eventCount)
    if (count === 0) return `Delete the category "${c.name}"?`
    const events = count === 1 ? 'Its 1 appointment' : `Its ${count} appointments`
    return `Delete the category "${c.name}"? ${events} will be kept — they just lose the category and show the default color.`
  }

  return (
    <div className="settings-card">
      <h2>Categories</h2>
      <p className="settings-sub">
        Categories are named colors for your appointments — Work, Family, Sports… Recolor one here and
        every appointment in it follows. They sync to your phone via the iCalendar CATEGORIES property.
      </p>

      <ul className="cal-list">
        {categories.map((c) => (
          <CategoryRow
            key={c.id}
            category={c}
            onSave={(name, color) => updateMut.mutate({ id: c.id, name, color })}
            onDelete={() => setConfirmDelete(c)}
          />
        ))}
      </ul>

      {confirmDelete && (
        <ConfirmDialog
          title="Delete category"
          message={confirmMessage(confirmDelete)}
          confirmLabel="Delete category"
          onConfirm={() => {
            deleteMut.mutate(confirmDelete.id)
            setConfirmDelete(null)
          }}
          onClose={() => setConfirmDelete(null)}
        />
      )}

      {adding ? (
        <form
          className="cal-add-row"
          onSubmit={(e) => {
            e.preventDefault()
            if (newName.trim()) createMut.mutate({ name: newName.trim(), color: newColor ?? nextColor() })
          }}
        >
          <label className="category-dot-pick" style={{ ['--sw']: newColor ?? nextColor() } as React.CSSProperties} title="Pick color">
            <input type="color" value={newColor ?? nextColor()} onChange={(e) => setNewColor(e.target.value)} />
          </label>
          {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
          <input
            value={newName}
            autoFocus
            placeholder="Category name"
            maxLength={100}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                setAdding(false)
                setNewName('')
                setNewColor(null)
              }
            }}
          />
          <button type="submit" className="btn-primary" disabled={!newName.trim() || createMut.isPending}>
            Add
          </button>
          <button type="button" className="btn-ghost" onClick={() => { setAdding(false); setNewName(''); setNewColor(null) }}>
            Cancel
          </button>
        </form>
      ) : (
        <button type="button" className="cal-add" onClick={() => setAdding(true)}>
          ＋ New category
        </button>
      )}

      {notice && <p className="error">{notice}</p>}
    </div>
  )
}

/** One category in the list: color dot (click to recolor), name (✎ to rename), count, delete. */
function CategoryRow({
  category,
  onSave,
  onDelete,
}: {
  category: CategoryDto
  onSave: (name: string, color: string) => void
  onDelete: () => void
}) {
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(category.name)

  const commit = () => {
    setEditing(false)
    const trimmed = name.trim()
    if (!trimmed || trimmed === category.name) {
      setName(category.name) // nothing to save (and never blank a name)
    } else {
      onSave(trimmed, category.color)
    }
  }

  const count = category.eventCount === 1 ? '1 appointment' : `${category.eventCount} appointments`

  return (
    <li className="cal-item">
      <label
        className="category-dot-pick"
        style={{ ['--sw']: category.color } as React.CSSProperties}
        title={`Recolor ${category.name}`}
      >
        <input
          type="color"
          value={category.color}
          onChange={(e) => onSave(category.name, e.target.value)}
        />
      </label>

      <span className="cal-item-body">
        {editing ? (
          // eslint-disable-next-line jsx-a11y/no-autofocus
          <input
            className="cal-item-rename"
            value={name}
            autoFocus
            maxLength={100}
            aria-label={`Rename ${category.name}`}
            onChange={(e) => setName(e.target.value)}
            onBlur={commit}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commit()
              if (e.key === 'Escape') {
                setName(category.name)
                setEditing(false)
              }
            }}
          />
        ) : (
          <span className="cal-item-name">{category.name}</span>
        )}
        <span className="cal-item-meta">{count}</span>
      </span>

      <span className="cal-item-actions">
        {!editing && (
          <button type="button" className="cal-item-action" onClick={() => setEditing(true)} aria-label={`Rename ${category.name}`} title="Rename">
            ✎
          </button>
        )}
        <button
          type="button"
          className="cal-item-action danger"
          onClick={onDelete}
          aria-label={`Delete ${category.name}`}
          title="Delete"
        >
          ✕
        </button>
      </span>
    </li>
  )
}

/**
 * The user's personal email account: the identity appointment invitations are sent from
 * (and, once inbox scanning ships, received into). Password is write-only.
 */
function EmailSection() {
  const queryClient = useQueryClient()
  const { data: account, isLoading } = useQuery({ queryKey: ['mail-account'], queryFn: getMailAccount })

  const [address, setAddress] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [smtpHost, setSmtpHost] = useState('')
  const [smtpPort, setSmtpPort] = useState(587)
  const [smtpUseSsl, setSmtpUseSsl] = useState(false)
  const [imapHost, setImapHost] = useState('')
  const [imapPort, setImapPort] = useState(993)
  const [imapUseSsl, setImapUseSsl] = useState(true)
  const [scanIntervalMinutes, setScanIntervalMinutes] = useState(5)
  const [loadedFor, setLoadedFor] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)

  // Populate the form once the stored account arrives (and again after disconnect).
  const accountKey = account ? account.address : account === null ? '' : 'loading'
  if (!isLoading && loadedFor !== accountKey) {
    setLoadedFor(accountKey)
    setAddress(account?.address ?? '')
    setUsername(account?.username ?? '')
    setPassword('')
    setSmtpHost(account?.smtpHost ?? '')
    setSmtpPort(Number(account?.smtpPort ?? 587))
    setSmtpUseSsl(account?.smtpUseSsl ?? false)
    setImapHost(account?.imapHost ?? '')
    setImapPort(Number(account?.imapPort ?? 993))
    setImapUseSsl(account?.imapUseSsl ?? true)
    setScanIntervalMinutes(Number(account?.scanIntervalMinutes ?? 5))
  }

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['mail-account'] })
  const saveMut = useMutation({
    mutationFn: saveMailAccount,
    onSuccess: () => { setNotice({ ok: true, text: 'Saved.' }); setPassword(''); refresh() },
    onError: (e) => setNotice({ ok: false, text: (e as Error).message }),
  })
  const testMut = useMutation({
    mutationFn: testMailAccount,
    onSuccess: (r) => setNotice(r.ok ? { ok: true, text: 'Connection works.' } : { ok: false, text: r.error ?? 'Connection failed.' }),
    onError: (e) => setNotice({ ok: false, text: (e as Error).message }),
  })
  const deleteMut = useMutation({
    mutationFn: deleteMailAccount,
    onSuccess: () => { setNotice({ ok: true, text: 'Disconnected.' }); refresh() },
    onError: (e) => setNotice({ ok: false, text: (e as Error).message }),
  })

  const canSave = address.trim() && smtpHost.trim() && username.trim() && (password || account?.hasPassword)

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!canSave) return
    saveMut.mutate({
      address: address.trim(),
      smtpHost: smtpHost.trim(),
      smtpPort,
      smtpUseSsl,
      imapHost: imapHost.trim() || null,
      imapPort,
      imapUseSsl,
      scanIntervalMinutes,
      username: username.trim(),
      password: password || null,
    })
  }

  return (
    <form className="settings-card" onSubmit={submit}>
      <h2>Email account</h2>
      <p className="settings-sub">
        Connect your own email address to send appointment invitations to guests — replies go straight to
        your inbox. Your password is stored encrypted on your server and never shown again.
      </p>

      <div className="mail-group">
        <span className="eyebrow">Account</span>
        <div className="field">
          <label htmlFor="ma-address">Email address</label>
          <input id="ma-address" type="email" value={address} placeholder="you@example.com" onChange={(e) => setAddress(e.target.value)} />
        </div>
        <div className="field-row">
          <div className="field">
            <label htmlFor="ma-username">Username</label>
            <input id="ma-username" value={username} placeholder="usually the address itself" onChange={(e) => setUsername(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="ma-password">Password</label>
            <input
              id="ma-password"
              type="password"
              value={password}
              placeholder={account?.hasPassword ? '•••••••• (unchanged)' : 'Mailbox or app password'}
              autoComplete="new-password"
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>
        </div>
        <p className="field-hint">
          For Gmail and most big providers you'll need an app password (requires two-factor auth).
        </p>
      </div>

      <div className="mail-group">
        <span className="eyebrow">Outgoing · SMTP</span>
        <div className="mail-server-row">
          <div className="field">
            <label htmlFor="ma-smtp-host">Server</label>
            <input id="ma-smtp-host" value={smtpHost} placeholder="smtp.example.com" onChange={(e) => setSmtpHost(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="ma-smtp-port">Port</label>
            <input id="ma-smtp-port" type="number" min={1} max={65535} value={smtpPort} onChange={(e) => setSmtpPort(Number(e.target.value))} />
          </div>
        </div>
        <label className="toggle">
          <input type="checkbox" checked={smtpUseSsl} onChange={(e) => setSmtpUseSsl(e.target.checked)} />
          <span>Implicit TLS (port 465) — off means STARTTLS (port 587)</span>
        </label>
      </div>

      <div className="mail-group">
        <span className="eyebrow">Incoming · IMAP</span>
        <div className="mail-server-row">
          <div className="field">
            <label htmlFor="ma-imap-host">Server</label>
            <input id="ma-imap-host" value={imapHost} placeholder="imap.example.com — optional" onChange={(e) => setImapHost(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="ma-imap-port">Port</label>
            <input id="ma-imap-port" type="number" min={1} max={65535} value={imapPort} onChange={(e) => setImapPort(Number(e.target.value))} />
          </div>
        </div>
        <label className="toggle">
          <input type="checkbox" checked={imapUseSsl} onChange={(e) => setImapUseSsl(e.target.checked)} />
          <span>IMAP over TLS (port 993)</span>
        </label>
        <div className="field mail-scan-field">
          <label htmlFor="ma-scan">Check for replies every</label>
          <div className="mail-scan-input">
            <input
              id="ma-scan"
              type="number"
              min={1}
              max={1440}
              value={scanIntervalMinutes}
              onChange={(e) => setScanIntervalMinutes(Math.min(1440, Math.max(1, Number(e.target.value) || 1)))}
            />
            <span>minutes</span>
          </div>
        </div>
        <p className="field-hint">
          When IMAP is set, your inbox is scanned on this interval for guests' Accept/Decline replies, and
          their status updates on the event automatically. Messages are only read, never changed or deleted.
        </p>
      </div>

      <div className="mail-actions">
        {account && (
          <button type="button" className="btn-danger" onClick={() => deleteMut.mutate()} disabled={deleteMut.isPending}>
            Disconnect
          </button>
        )}
        <span className="spacer" />
        <button type="button" className="btn-ghost" onClick={() => testMut.mutate()} disabled={!account || testMut.isPending}>
          {testMut.isPending ? 'Testing…' : 'Test connection'}
        </button>
        <button type="submit" className="btn-primary" disabled={!canSave || saveMut.isPending}>
          {saveMut.isPending ? 'Saving…' : 'Save'}
        </button>
      </div>

      {notice && <p className={notice.ok ? 'mail-notice-ok' : 'error'}>{notice.text}</p>}
    </form>
  )
}

/** Sync via CalDAV: shows the per-instance connection details and a client-agnostic walkthrough. */
function SyncSection() {
  const { data: profile } = useQuery({ queryKey: ['profile'], queryFn: getProfile })
  const davUrl = `${window.location.origin}/dav/`
  const [copied, setCopied] = useState<string | null>(null)

  const copy = async (label: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(label)
      setTimeout(() => setCopied(null), 1500)
    } catch {
      /* clipboard unavailable (e.g. plain-http origin) — the value is still selectable */
    }
  }

  const row = (label: string, value: string) => (
    <div className="settings-row">
      <span className="settings-row-label">{label}</span>
      <span className="settings-row-value sync-value">{value}</span>
      <button type="button" className="link-btn" onClick={() => copy(label, value)}>
        {copied === label ? 'Copied ✓' : 'Copy'}
      </button>
    </div>
  )

  return (
    <>
      <div className="settings-card">
        <h2>Sync your calendar</h2>
        <p className="settings-sub">
          CalendarIT speaks <strong>CalDAV</strong>, an open standard — any app or device that supports CalDAV
          can sync your calendar two-way. Events created or edited on either side show up on the other.
        </p>

        <div className="settings-rows">
          {row('Server URL', davUrl)}
          {profile?.email && row('Username', profile.email)}
          <div className="settings-row">
            <span className="settings-row-label">Password</span>
            <span className="settings-row-value">your CalendarIT account password</span>
          </div>
        </div>
      </div>

      <div className="settings-card">
        <h2>Connect a CalDAV client</h2>
        <ol className="sync-steps">
          <li>In your calendar app (or a dedicated sync app), add a new <strong>CalDAV account</strong>.</li>
          <li>
            Enter the server URL and username from above — many clients can also auto-discover the server
            from just the hostname.
          </li>
          <li>Enter your CalendarIT password.</li>
          <li>Enable the <strong>Personal</strong> calendar and grant calendar permissions if asked.</li>
          <li>Done — your events appear in the client, and edits sync back here.</li>
        </ol>
        <p className="field-hint">
          Note: if this page is served over plain HTTP, clients will warn that the password travels
          unencrypted — put the server behind HTTPS for anything beyond your own LAN.
        </p>
      </div>
    </>
  )
}

const navClass = (active: boolean) => 'settings-nav-item' + (active ? ' active' : '')

/* --- inline icons -------------------------------------------------------- */
const iconProps = { width: 18, height: 18, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }

function ChevronLeft() {
  return <svg {...iconProps}><path d="M15 18l-6-6 6-6" /></svg>
}
function UserIcon() {
  return <svg {...iconProps}><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" /><circle cx="12" cy="7" r="4" /></svg>
}
function ShieldIcon() {
  return <svg {...iconProps}><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" /></svg>
}
function MailIcon() {
  return <svg {...iconProps}><rect x="2" y="4" width="20" height="16" rx="2" /><path d="M22 7l-10 6L2 7" /></svg>
}
function CalendarIcon() {
  return <svg {...iconProps}><rect x="3" y="4" width="18" height="18" rx="2" /><path d="M16 2v4M8 2v4M3 10h18" /></svg>
}
function TagIcon() {
  return <svg {...iconProps}><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z" /><path d="M7 7h.01" /></svg>
}
function PhoneIcon() {
  return <svg {...iconProps}><rect x="5" y="2" width="14" height="20" rx="2" /><path d="M12 18h.01" /></svg>
}
function SignOutIcon() {
  return <svg {...iconProps}><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" /><path d="M16 17l5-5-5-5" /><path d="M21 12H9" /></svg>
}
function CameraIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z" />
      <circle cx="12" cy="13" r="4" />
    </svg>
  )
}
