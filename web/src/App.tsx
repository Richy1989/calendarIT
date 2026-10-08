import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api/client'
import { getProfile } from './api/profile'
import { getAuthConfig, requestPasswordReset, resetPassword } from './api/password'
import { getVisibleCalendars, getVisibleCategories, saveVisibleCalendars, saveVisibleCategories } from './prefs'
import { getTokens, onTokensChangedElsewhere, setTokens, type AuthTokens } from './auth/authStorage'
import { signOut } from './auth/session'
import { clearEventClipboard } from './lib/eventClipboard'
import { getNotifyMode } from './push/webPush'
import { startLocalReminderPoller, stopLocalReminderPoller } from './push/localReminders'
import CalendarView from './CalendarView'
import { ClockProvider, useHour12 } from './clock'
import { WeekStartProvider } from './weekStart'
import { formatTime } from './lib/dates'
import SearchBar from './SearchBar'
import ProfileMenu from './ProfileMenu'
import SettingsPage from './SettingsPage'
import Logo from './Logo'
import './App.css'

type Mode = 'login' | 'register'

/**
 * The reset link lands on /reset-password?email=…&token=…. There's no router here, so the
 * parameters are read straight off the URL once, before anything renders.
 */
function readResetRequest(): { email: string; token: string } | null {
  if (window.location.pathname !== '/reset-password') return null
  const params = new URLSearchParams(window.location.search)
  const email = params.get('email')
  const token = params.get('token')
  return email && token ? { email, token } : null
}

/** Drops the token from the address bar so it isn't left in history or copied by accident. */
function clearResetUrl() {
  window.history.replaceState(null, '', '/')
}

export default function App() {
  // All hooks must run unconditionally and in a stable order — keep them above any
  // early return, or the hook count changes between logged-out/in renders and React throws.
  const [tokens, setAuth] = useState<AuthTokens | null>(getTokens())
  // null = calendar screen; otherwise the settings page, opened on that section.
  const [settingsSection, setSettingsSection] = useState<'general' | 'calendars' | 'categories' | null>(null)
  // A search pick sets this; CalendarView watches it and jumps to that day. The bumping
  // counter lets picking the same date twice re-trigger the navigation.
  const [focus, setFocus] = useState<{ date: string; n: number } | null>(null)
  // Which calendars/categories are shown: null = all. Persisted locally so the choice sticks.
  const [visibleCals, setVisibleCals] = useState<string[] | null>(getVisibleCalendars())
  const [visibleCats, setVisibleCats] = useState<string[] | null>(getVisibleCategories())
  const { data: profile } = useQuery({ queryKey: ['profile'], queryFn: getProfile, enabled: !!tokens })

  const queryClient = useQueryClient()

  // Every cached query belongs to whoever was signed in. Dropping the cache whenever the account
  // changes is what stops the next person at this browser from briefly seeing the last one's
  // calendar (queries aren't keyed by user, and stay "fresh" for half a minute).
  const signedIn = (t: AuthTokens) => {
    queryClient.clear()
    setTokens(t)
    setAuth(t)
  }

  const signedOut = () => {
    stopLocalReminderPoller()
    clearEventClipboard()
    queryClient.clear()
    setAuth(null)
  }

  const logout = () => {
    void signOut() // revokes the session server-side; local state goes right away
    signedOut()
  }

  // The listeners below are registered once; the ref keeps them calling the latest signedOut.
  const signedOutRef = useRef(signedOut)
  signedOutRef.current = signedOut

  // If the refresh token has also expired, session.ts clears storage and fires this —
  // drop back to the login screen instead of leaving a dead session.
  useEffect(() => {
    const onExpired = () => signedOutRef.current()
    window.addEventListener('auth-expired', onExpired)
    return () => window.removeEventListener('auth-expired', onExpired)
  }, [])

  // Signing in or out in one tab does the same in the others.
  useEffect(
    () =>
      onTokensChangedElsewhere((next) => {
        if (!next) signedOutRef.current()
        else setAuth((current) => (current?.refreshToken === next.refreshToken ? current : next))
      }),
    [],
  )

  // While signed in on a browser that fell back to local notifications, poll for due reminders
  // and show them. Push-mode browsers are served by the backend job and don't poll.
  useEffect(() => {
    if (!tokens || getNotifyMode() !== 'local') {
      stopLocalReminderPoller()
      return
    }
    startLocalReminderPoller()
    return () => stopLocalReminderPoller()
  }, [tokens])

  if (!tokens) {
    return <AuthGate onAuthenticated={signedIn} />
  }

  if (settingsSection) {
    return (
      <ClockProvider serverUse24Hour={profile?.use24HourClock ?? null}>
        {/* No `?? null` here: undefined means "profile still loading", which the provider has to
            tell apart from a stored null ("follow my locale") so it doesn't clear the cache. */}
        <WeekStartProvider serverWeekStart={profile?.weekStart}>
          <SettingsPage
            initialSection={settingsSection}
            onBack={() => setSettingsSection(null)}
            onLogout={logout}
          />
        </WeekStartProvider>
      </ClockProvider>
    )
  }

  return (
    <ClockProvider serverUse24Hour={profile?.use24HourClock ?? null}>
    <WeekStartProvider serverWeekStart={profile?.weekStart}>
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <Logo />
          <span className="brand-word">
            Calendar<b>IT</b>
          </span>
        </div>
        <div className="header-search">
          <SearchBar onPick={(date) => setFocus((f) => ({ date, n: (f?.n ?? 0) + 1 }))} />
        </div>
        <div className="header-actions">
          <LiveDateTime />
          <ProfileMenu
            email={profile?.email}
            avatarUrl={profile?.avatarDataUrl}
            onOpenSettings={() => setSettingsSection('general')}
            onLogout={logout}
          />
        </div>
      </header>
      <main className="app-main">
        <section className="calendar-shell">
          <CalendarView
            focus={focus}
            serverView={profile?.defaultView ?? null}
            visibleCalendarIds={visibleCals}
            onChangeVisible={(ids) => {
              setVisibleCals(ids)
              saveVisibleCalendars(ids)
            }}
            onManage={() => setSettingsSection('calendars')}
            visibleCategoryIds={visibleCats}
            onChangeVisibleCategories={(ids) => {
              setVisibleCats(ids)
              saveVisibleCategories(ids)
            }}
            onManageCategories={() => setSettingsSection('categories')}
          />
        </section>
      </main>
    </div>
    </WeekStartProvider>
    </ClockProvider>
  )
}

// Live weekday + date + ticking clock, on the right of the header. Kept in its own
// component so the per-second tick re-renders only this span, not the calendar below it.
function LiveDateTime() {
  const hour12 = useHour12()
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(id)
  }, [])

  const date = now.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' })
  const time = formatTime(now, hour12, true)

  return (
    <span className="eyebrow">
      <span>{date}</span>
      <time className="clock">{time}</time>
    </span>
  )
}

function AuthGate({ onAuthenticated }: { onAuthenticated: (t: AuthTokens) => void }) {
  // Arriving on a reset link replaces the sign-in form until it's dealt with.
  const [resetRequest, setResetRequest] = useState(readResetRequest)
  const [forgot, setForgot] = useState(false)
  const { data: authConfig } = useQuery({ queryKey: ['auth-config'], queryFn: getAuthConfig })
  const registrationEnabled = authConfig?.registrationEnabled ?? true
  const [mode, setMode] = useState<Mode>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  // Held back until the field is left (or submit is attempted) so the mismatch warning doesn't
  // sit there accusing you of a typo while you're still typing the second password.
  const [confirmBlurred, setConfirmBlurred] = useState(false)

  const mutation = useMutation({
    mutationFn: async () => {
      const body = { email, password }
      const { data, error } =
        mode === 'login'
          ? await api.POST('/api/auth/login', { body })
          : await api.POST('/api/auth/register', { body })
      if (error || !data) {
        throw new Error(extractError(error) ?? 'Something went wrong. Try again.')
      }
      return data
    },
    onSuccess: (data) => onAuthenticated(data),
  })

  // An instance with sign-up closed shouldn't offer a tab that can only fail.
  const isLogin = mode === 'login' || !registrationEnabled
  const mismatch = !isLogin && confirm.length > 0 && confirm !== password
  const showMismatch = mismatch && confirmBlurred

  if (resetRequest) {
    return (
      <ResetPasswordForm
        request={resetRequest}
        onDone={() => {
          clearResetUrl()
          setResetRequest(null)
        }}
      />
    )
  }

  if (forgot) {
    return <ForgotPasswordForm onBack={() => setForgot(false)} />
  }

  // Switching tabs starts a clean form: a half-typed confirmation (and any stale error) has
  // nothing to do with the mode you just moved to.
  const switchTo = (next: Mode) => {
    setMode(next)
    setConfirm('')
    setConfirmBlurred(false)
    mutation.reset()
  }

  return (
    <div className="auth">
      <form
        className="auth-card"
        onSubmit={(e) => {
          e.preventDefault()
          if (mismatch) {
            setConfirmBlurred(true) // surface the reason nothing happened
            return
          }
          mutation.mutate()
        }}
      >
        <div className="auth-head">
          <Logo />
          <span className="eyebrow">Self-hosted · CalDAV-ready</span>
          <h1 className="auth-title">{isLogin ? 'Welcome back' : 'Create your account'}</h1>
        </div>

        {registrationEnabled && (
          <div className="segmented" role="tablist">
            <button type="button" role="tab" aria-selected={isLogin} className={isLogin ? 'active' : ''} onClick={() => switchTo('login')}>
              Log in
            </button>
            <button type="button" role="tab" aria-selected={!isLogin} className={!isLogin ? 'active' : ''} onClick={() => switchTo('register')}>
              Register
            </button>
          </div>
        )}

        <div className="form">
          <div className="field">
            <label htmlFor="email">Email</label>
            <input
              id="email"
              type="email"
              autoComplete="email"
              placeholder="you@example.com"
              value={email}
              required
              onChange={(e) => setEmail(e.target.value)}
            />
          </div>
          <div className="field">
            <label htmlFor="password">Password</label>
            <input
              id="password"
              type="password"
              autoComplete={isLogin ? 'current-password' : 'new-password'}
              placeholder={isLogin ? 'Your password' : 'At least 8 characters'}
              value={password}
              required
              minLength={8}
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>

          {/* Typed twice when registering: a typo in a password you can't see would otherwise
              lock you out of the account you just made. Login has nothing to confirm. */}
          {!isLogin && (
            <div className="field">
              <label htmlFor="password-confirm">Confirm password</label>
              <input
                id="password-confirm"
                type="password"
                autoComplete="new-password"
                placeholder="Repeat your password"
                value={confirm}
                required
                aria-invalid={showMismatch}
                aria-describedby={showMismatch ? 'password-confirm-error' : undefined}
                onChange={(e) => setConfirm(e.target.value)}
                onBlur={() => setConfirmBlurred(true)}
              />
              {showMismatch && (
                <p id="password-confirm-error" className="field-error" role="alert">
                  The passwords don't match.
                </p>
              )}
            </div>
          )}

          {mutation.isError && <p className="error">{(mutation.error as Error).message}</p>}

          <button className="btn-primary" type="submit" disabled={mutation.isPending}>
            {mutation.isPending ? (isLogin ? 'Signing in…' : 'Creating account…') : isLogin ? 'Sign in' : 'Create account'}
          </button>

          {isLogin && (
            <button type="button" className="link-button" onClick={() => setForgot(true)}>
              Forgot your password?
            </button>
          )}
        </div>

        <p className="auth-foot">Your calendar data stays on your own server.</p>
      </form>
    </div>
  )
}

/** Asks for a reset link. Always reports success — whether that address has an account here
 *  is not something an unauthenticated screen should reveal. */
function ForgotPasswordForm({ onBack }: { onBack: () => void }) {
  const [email, setEmail] = useState('')
  const mutation = useMutation({ mutationFn: () => requestPasswordReset(email) })

  return (
    <div className="auth">
      <form
        className="auth-card"
        onSubmit={(e) => {
          e.preventDefault()
          mutation.mutate()
        }}
      >
        <div className="auth-head">
          <Logo />
          <span className="eyebrow">Account recovery</span>
          <h1 className="auth-title">Reset your password</h1>
        </div>

        {mutation.isSuccess ? (
          <div className="form">
            <p className="auth-note">
              If an account exists for <strong>{email}</strong>, a reset link is on its way. The link
              works once and expires in two hours.
            </p>
            <p className="field-hint">
              No email arriving? Reset mail is sent through the mail account connected in Settings →
              Email. Without one, the server writes the link to its log instead — check
              <code> docker logs</code>.
            </p>
            <button type="button" className="btn-primary" onClick={onBack}>
              Back to sign in
            </button>
          </div>
        ) : (
          <div className="form">
            <div className="field">
              <label htmlFor="forgot-email">Email</label>
              {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
              <input
                id="forgot-email"
                type="email"
                autoComplete="email"
                placeholder="you@example.com"
                value={email}
                required
                autoFocus
                onChange={(e) => setEmail(e.target.value)}
              />
            </div>

            {mutation.isError && <p className="error">{(mutation.error as Error).message}</p>}

            <button className="btn-primary" type="submit" disabled={mutation.isPending}>
              {mutation.isPending ? 'Sending…' : 'Send reset link'}
            </button>
            <button type="button" className="link-button" onClick={onBack}>
              Back to sign in
            </button>
          </div>
        )}
      </form>
    </div>
  )
}

/** Sets a new password from an emailed link. */
function ResetPasswordForm({
  request,
  onDone,
}: {
  request: { email: string; token: string }
  onDone: () => void
}) {
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [confirmBlurred, setConfirmBlurred] = useState(false)
  const mismatch = confirm.length > 0 && confirm !== password
  const showMismatch = mismatch && confirmBlurred

  const mutation = useMutation({
    mutationFn: () => resetPassword(request.email, request.token, password),
  })

  return (
    <div className="auth">
      <form
        className="auth-card"
        onSubmit={(e) => {
          e.preventDefault()
          if (mismatch) {
            setConfirmBlurred(true)
            return
          }
          mutation.mutate()
        }}
      >
        <div className="auth-head">
          <Logo />
          <span className="eyebrow">Account recovery</span>
          <h1 className="auth-title">Choose a new password</h1>
        </div>

        {mutation.isSuccess ? (
          <div className="form">
            <p className="auth-note">
              Your password has been changed, and every existing session was signed out. Sign in with
              the new one.
            </p>
            <button type="button" className="btn-primary" onClick={onDone}>
              Go to sign in
            </button>
          </div>
        ) : (
          <div className="form">
            <p className="auth-note">
              Setting a new password for <strong>{request.email}</strong>.
            </p>
            <div className="field">
              <label htmlFor="reset-password">New password</label>
              {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
              <input
                id="reset-password"
                type="password"
                autoComplete="new-password"
                placeholder="At least 8 characters"
                value={password}
                required
                minLength={8}
                autoFocus
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="reset-confirm">Confirm password</label>
              <input
                id="reset-confirm"
                type="password"
                autoComplete="new-password"
                placeholder="Repeat your password"
                value={confirm}
                required
                aria-invalid={showMismatch}
                aria-describedby={showMismatch ? 'reset-confirm-error' : undefined}
                onChange={(e) => setConfirm(e.target.value)}
                onBlur={() => setConfirmBlurred(true)}
              />
              {showMismatch && (
                <p id="reset-confirm-error" className="field-error" role="alert">
                  The passwords don't match.
                </p>
              )}
            </div>

            {mutation.isError && <p className="error">{(mutation.error as Error).message}</p>}

            <button className="btn-primary" type="submit" disabled={mutation.isPending}>
              {mutation.isPending ? 'Saving…' : 'Set new password'}
            </button>
            <button type="button" className="link-button" onClick={onDone}>
              Cancel
            </button>
          </div>
        )}
      </form>
    </div>
  )
}

function extractError(error: unknown): string | undefined {
  if (error && typeof error === 'object' && 'errors' in error) {
    const errors = (error as { errors?: unknown }).errors
    if (Array.isArray(errors) && errors.length > 0) return String(errors[0])
  }
  return undefined
}
