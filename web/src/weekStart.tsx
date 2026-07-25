import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { saveWeekStart } from './api/profile'

// One place decides which day the week starts on, for every grid in the app. Like the 12h/24h
// clock it's a server-side profile preference (so a phone and a laptop agree), cached in
// localStorage so the very first paint draws the right grid — a calendar that renders
// Sunday-first and then jumps to Monday is worse than either choice on its own.

const KEY = 'calendarit.weekStart'

/** null = automatic: follow whatever the browser's locale does. */
export type WeekStartPref = 'sunday' | 'monday' | null

/** The day a grid starts on, in JS `getDay()` terms: 0 = Sunday, 1 = Monday. */
export type FirstDay = 0 | 1

type WeekInfo = { firstDay: number }
type LocaleWeekInfo = Intl.Locale & { getWeekInfo?: () => WeekInfo; weekInfo?: WeekInfo }

// Fallback for engines without getWeekInfo(). Most of the world starts on Monday; these are the
// regions that don't. Saturday-first regions (much of the Middle East) are included because
// Sunday is the closer of the two days we offer.
const SUNDAY_FIRST_REGIONS = new Set([
  'AG', 'AS', 'AU', 'BD', 'BR', 'BS', 'BT', 'BW', 'BZ', 'CA', 'CN', 'CO', 'DM', 'DO', 'ET', 'GT',
  'GU', 'HK', 'HN', 'ID', 'IL', 'IN', 'JM', 'JP', 'KE', 'KH', 'KR', 'LA', 'MH', 'MM', 'MO', 'MT',
  'MX', 'MZ', 'NI', 'NP', 'PA', 'PE', 'PH', 'PK', 'PR', 'PT', 'PY', 'SA', 'SG', 'SV', 'TH', 'TT',
  'TW', 'UM', 'US', 'VE', 'VI', 'WS', 'YE', 'ZA', 'ZW',
])

/**
 * The week start a locale implies. Prefers the browser's own answer (`getWeekInfo`), which is
 * authoritative but not yet everywhere, and otherwise reads the region out of the tag.
 *
 * Only Sunday and Monday come out of this: those are the two the setting offers, so a
 * Saturday-first locale is reported as Sunday rather than as something unrepresentable.
 */
export function localeFirstDay(locale: string): FirstDay {
  try {
    const info = (() => {
      const l = new Intl.Locale(locale) as LocaleWeekInfo
      return l.getWeekInfo?.() ?? l.weekInfo
    })()
    // getWeekInfo is ISO-numbered: 1 = Monday … 7 = Sunday.
    if (info) return info.firstDay === 1 ? 1 : 0

    const region = new Intl.Locale(locale).region
    return region && SUNDAY_FIRST_REGIONS.has(region) ? 0 : 1
  } catch {
    return 1 // an unparseable tag is no reason to fail; Monday is the commoner default
  }
}

/** Resolves a stored preference to the day grids actually start on. */
export function resolveFirstDay(pref: WeekStartPref, locale: string): FirstDay {
  if (pref === 'sunday') return 0
  if (pref === 'monday') return 1
  return localeFirstDay(locale)
}

function browserLocale(): string {
  return new Intl.DateTimeFormat().resolvedOptions().locale
}

function getCachedPref(): WeekStartPref {
  const raw = localStorage.getItem(KEY)
  return raw === 'sunday' || raw === 'monday' ? raw : null
}

type WeekStartCtx = { pref: WeekStartPref; firstDay: FirstDay; setPref: (v: WeekStartPref) => void }
const Ctx = createContext<WeekStartCtx>({ pref: null, firstDay: 1, setPref: () => {} })

/** The day calendar grids start on: 0 = Sunday, 1 = Monday. Always resolved, never null. */
export function useFirstDay(): FirstDay {
  return useContext(Ctx).firstDay
}

/** The raw preference plus its setter, for the Settings control. */
export function useWeekStart(): WeekStartCtx {
  return useContext(Ctx)
}

export function WeekStartProvider({
  serverWeekStart,
  children,
}: {
  /** The profile's stored preference; null/undefined means automatic (or not loaded yet). */
  serverWeekStart?: string | null
  children: ReactNode
}) {
  const [pref, setPrefState] = useState<WeekStartPref>(getCachedPref)

  // The server profile is the cross-device source of truth. Note this adopts null too: clearing
  // the setting on another device has to propagate, not be masked by a stale local value.
  useEffect(() => {
    if (serverWeekStart === undefined) return
    const next: WeekStartPref =
      serverWeekStart === 'sunday' || serverWeekStart === 'monday' ? serverWeekStart : null
    setPrefState(next)
    if (next) localStorage.setItem(KEY, next)
    else localStorage.removeItem(KEY)
  }, [serverWeekStart])

  const setPref = useCallback((v: WeekStartPref) => {
    setPrefState(v)
    if (v) localStorage.setItem(KEY, v)
    else localStorage.removeItem(KEY)
    saveWeekStart(v).catch(() => {}) // fire-and-forget cross-device persistence
  }, [])

  const firstDay = resolveFirstDay(pref, browserLocale())

  return <Ctx.Provider value={{ pref, firstDay, setPref }}>{children}</Ctx.Provider>
}
