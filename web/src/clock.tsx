import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { saveClockFormat } from './api/profile'

// One place decides 12h vs 24h for the whole app. It's a server-side profile preference
// (cross-device, like the default view), with a localStorage cache so the very first paint
// picks the right format synchronously — no flash of the wrong clock while the profile loads.

const KEY = 'calendarit.hour12'

/** The browser locale's own convention, used until the user (or their saved profile) says otherwise. */
function localeHour12(): boolean {
  return new Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions().hour12 ?? true
}

function getCachedHour12(): boolean {
  const raw = localStorage.getItem(KEY)
  if (raw === 'true') return true
  if (raw === 'false') return false
  return localeHour12()
}

type ClockCtx = { hour12: boolean; setHour12: (v: boolean) => void }
const Ctx = createContext<ClockCtx>({ hour12: true, setHour12: () => {} })

/** True when times should render 12-hour (1:00 PM); false for 24-hour (13:00). */
export function useHour12(): boolean {
  return useContext(Ctx).hour12
}

/** The clock preference plus its setter (for the Settings toggle). */
export function useClock(): ClockCtx {
  return useContext(Ctx)
}

export function ClockProvider({
  serverUse24Hour,
  children,
}: {
  /** The profile's stored preference: true = 24h, false = 12h, null/undefined = not set yet. */
  serverUse24Hour?: boolean | null
  children: ReactNode
}) {
  const [hour12, setHour12State] = useState<boolean>(getCachedHour12)

  // The server profile is the cross-device source of truth: once it loads with an explicit
  // value, adopt it and refresh the local cache for next time's first paint.
  useEffect(() => {
    if (serverUse24Hour === null || serverUse24Hour === undefined) return
    const h12 = !serverUse24Hour
    setHour12State(h12)
    localStorage.setItem(KEY, String(h12))
  }, [serverUse24Hour])

  const setHour12 = useCallback((v: boolean) => {
    setHour12State(v)
    localStorage.setItem(KEY, String(v))
    saveClockFormat(!v).catch(() => {}) // fire-and-forget cross-device persistence
  }, [])

  return <Ctx.Provider value={{ hour12, setHour12 }}>{children}</Ctx.Provider>
}
