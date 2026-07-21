import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { deleteAvatar, getProfile, uploadAvatar } from './api/profile'
import { exportIcs, importIcs } from './api/events'
import Logo from './Logo'

type Section = 'general' | 'security' | 'email'

/** Dedicated, full-page settings screen: sidebar nav + a content card per section. */
export default function SettingsPage({ onBack, onLogout }: { onBack: () => void; onLogout: () => void }) {
  const [section, setSection] = useState<Section>('general')

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
          ) : (
            <div className="settings-card settings-placeholder">
              <h2>{section === 'security' ? 'Security' : 'Email'}</h2>
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

  const handleExport = async () => {
    try {
      const blob = await exportIcs()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'calendarit.ics'
      a.click()
      URL.revokeObjectURL(url)
    } catch {
      setDataNotice('Export failed.')
    }
  }

  const handleImport = () => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = '.ics,text/calendar'
    input.onchange = async () => {
      const chosen = input.files?.[0]
      if (!chosen) return
      try {
        const result = await importIcs(await chosen.text())
        await queryClient.invalidateQueries({ queryKey: ['events'] })
        setDataNotice(`Imported ${result.imported}, skipped ${result.skipped}.`)
      } catch {
        setDataNotice('Import failed — is it a valid .ics file?')
      }
    }
    input.click()
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
      {dataNotice && <p className="field-hint">{dataNotice}</p>}
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
