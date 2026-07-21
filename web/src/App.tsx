import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api } from './api/client'
import { getTokens, setTokens, type AuthTokens } from './auth/authStorage'
import CalendarView from './CalendarView'
import Logo from './Logo'
import './App.css'

type Mode = 'login' | 'register'

export default function App() {
  const [tokens, setAuth] = useState<AuthTokens | null>(getTokens())

  const persist = (t: AuthTokens | null) => {
    setTokens(t)
    setAuth(t)
  }

  if (!tokens) {
    return <AuthGate onAuthenticated={persist} />
  }

  const today = new Date().toLocaleDateString(undefined, {
    weekday: 'long',
    month: 'short',
    day: 'numeric',
  })

  return (
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <Logo />
          <span className="brand-word">
            Calendar<b>IT</b>
          </span>
        </div>
        <button className="btn-ghost" onClick={() => persist(null)}>
          Log out
        </button>
      </header>
      <main className="app-main">
        <div className="section-lead">
          <h2>Your schedule</h2>
          <span className="eyebrow">{today}</span>
        </div>
        <section className="calendar-shell">
          <CalendarView />
        </section>
      </main>
    </div>
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
