import { useEffect, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api } from './api/client'
import { getProfile } from './api/profile'
import { getTokens, setTokens, type AuthTokens } from './auth/authStorage'
import CalendarView from './CalendarView'
import SearchBar from './SearchBar'
import ProfileMenu from './ProfileMenu'
import SettingsPage from './SettingsPage'
import Logo from './Logo'
import './App.css'

type Mode = 'login' | 'register'

export default function App() {
  // All hooks must run unconditionally and in a stable order — keep them above any
  // early return, or the hook count changes between logged-out/in renders and React throws.
  const [tokens, setAuth] = useState<AuthTokens | null>(getTokens())
  const [showSettings, setShowSettings] = useState(false)
  // A search pick sets this; CalendarView watches it and jumps to that day. The bumping
  // counter lets picking the same date twice re-trigger the navigation.
  const [focus, setFocus] = useState<{ date: string; n: number } | null>(null)
  const { data: profile } = useQuery({ queryKey: ['profile'], queryFn: getProfile, enabled: !!tokens })

  const persist = (t: AuthTokens | null) => {
    setTokens(t)
    setAuth(t)
  }

  // If the refresh token has also expired, session.ts clears storage and fires this —
  // drop back to the login screen instead of leaving a dead session.
  useEffect(() => {
    const onExpired = () => setAuth(null)
    window.addEventListener('auth-expired', onExpired)
    return () => window.removeEventListener('auth-expired', onExpired)
  }, [])

  if (!tokens) {
    return <AuthGate onAuthenticated={persist} />
  }

  if (showSettings) {
    return <SettingsPage onBack={() => setShowSettings(false)} onLogout={() => persist(null)} />
  }

  return (
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
          <ProfileMenu
            email={profile?.email}
            avatarUrl={profile?.avatarDataUrl}
            onOpenSettings={() => setShowSettings(true)}
            onLogout={() => persist(null)}
          />
        </div>
      </header>
      <main className="app-main">
        <div className="section-lead">
          <h2>Your schedule</h2>
          <LiveDateTime />
        </div>
        <section className="calendar-shell">
          <CalendarView focus={focus} serverView={profile?.defaultView ?? null} />
        </section>
      </main>
    </div>
  )
}

// Live weekday + date + ticking clock, on the right of the section header. Kept in its own
// component so the per-second tick re-renders only this span, not the calendar below it.
function LiveDateTime() {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(id)
  }, [])

  const date = now.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' })
  const time = now.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })

  return (
    <span className="eyebrow">
      <span>{date}</span>
      <time className="clock">{time}</time>
    </span>
  )
}

function AuthGate({ onAuthenticated }: { onAuthenticated: (t: AuthTokens) => void }) {
  const [mode, setMode] = useState<Mode>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')

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

  const isLogin = mode === 'login'

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
          <span className="eyebrow">Self-hosted · CalDAV-ready</span>
          <h1 className="auth-title">{isLogin ? 'Welcome back' : 'Create your account'}</h1>
        </div>

        <div className="segmented" role="tablist">
          <button type="button" role="tab" aria-selected={isLogin} className={isLogin ? 'active' : ''} onClick={() => setMode('login')}>
            Log in
          </button>
          <button type="button" role="tab" aria-selected={!isLogin} className={!isLogin ? 'active' : ''} onClick={() => setMode('register')}>
            Register
          </button>
        </div>

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

          {mutation.isError && <p className="error">{(mutation.error as Error).message}</p>}

          <button className="btn-primary" type="submit" disabled={mutation.isPending}>
            {mutation.isPending ? (isLogin ? 'Signing in…' : 'Creating account…') : isLogin ? 'Sign in' : 'Create account'}
          </button>
        </div>

        <p className="auth-foot">Your calendar data stays on your own server.</p>
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
