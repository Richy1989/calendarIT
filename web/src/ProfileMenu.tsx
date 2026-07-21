import { useEffect, useState } from 'react'

/**
 * Avatar button in the top-left that opens the account menu (Settings, Log out).
 * Falls back to the email's initial on a gradient when there's no picture.
 */
export default function ProfileMenu({
  email,
  avatarUrl,
  onOpenSettings,
  onLogout,
}: {
  email?: string | null
  avatarUrl?: string | null
  onOpenSettings: () => void
  onLogout: () => void
}) {
  const [open, setOpen] = useState(false)
  const initial = (email?.trim()?.[0] ?? '?').toUpperCase()

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  return (
    <div className="profile">
      <button
        className="avatar-btn"
        onClick={() => setOpen((o) => !o)}
        aria-label="Account menu"
        aria-haspopup="menu"
        aria-expanded={open}
      >
        {avatarUrl ? <img src={avatarUrl} alt="" /> : <span className="avatar-fallback">{initial}</span>}
      </button>

      {open && (
        <>
          <div className="menu-backdrop" onMouseDown={() => setOpen(false)} />
          <div className="profile-menu" role="menu">
            {email && <div className="profile-menu-email">{email}</div>}
            <button className="ctx-item" role="menuitem" onClick={() => { setOpen(false); onOpenSettings() }}>
              Settings
            </button>
            <button className="ctx-item" role="menuitem" onClick={() => { setOpen(false); onLogout() }}>
              Log out
            </button>
          </div>
        </>
      )}
    </div>
  )
}
