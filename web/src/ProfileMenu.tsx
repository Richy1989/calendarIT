import { useEffect, useState } from 'react'

/**
 * Avatar button in the top-left that opens the account menu (Settings, Log out).
 * Falls back to the email's initial on a gradient when there's no picture.
 */
export default function ProfileMenu({
  email,
  avatarUrl,
  pendingAccounts = 0,
  onOpenSettings,
  onLogout,
}: {
  email?: string | null
  avatarUrl?: string | null
  /** For the admin: accounts waiting for activation (a badge, and a hint on Settings). */
  pendingAccounts?: number
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
        aria-label={pendingAccounts > 0 ? `Account menu, ${pendingAccounts} new accounts waiting` : 'Account menu'}
        aria-haspopup="menu"
        aria-expanded={open}
      >
        {avatarUrl ? <img src={avatarUrl} alt="" /> : <span className="avatar-fallback">{initial}</span>}
      </button>
      {pendingAccounts > 0 && (
        <span className="profile-badge" aria-hidden="true">
          {pendingAccounts > 9 ? '9+' : pendingAccounts}
        </span>
      )}

      {open && (
        <>
          <div className="menu-backdrop" onMouseDown={() => setOpen(false)} />
          <div className="profile-menu" role="menu">
            {email && <div className="profile-menu-email">{email}</div>}
            <button className="ctx-item" role="menuitem" onClick={() => { setOpen(false); onOpenSettings() }}>
              Settings
              {pendingAccounts > 0 && (
                <span className="profile-menu-hint">
                  {pendingAccounts === 1 ? '1 new account waiting' : `${pendingAccounts} new accounts waiting`}
                </span>
              )}
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
