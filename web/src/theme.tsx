import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { saveAppearance } from './api/profile'
import { AppearanceContext, applyAppearance, cacheAppearance, getCachedAppearance, normalizeAppearance, type Appearance } from './lib/appearance'

/** Holds the app's look (lib/appearance.ts), keeps it in step with the profile, and applies it. */
export function ThemeProvider({
  serverAppearance,
  children,
}: {
  /** The profile's stored look; undefined while it loads (keep the cached one meanwhile). */
  serverAppearance?: { theme?: string | null; accentColor?: string | null; highlightColor?: string | null }
  children: ReactNode
}) {
  const [appearance, setState] = useState<Appearance>(getCachedAppearance)

  // The server profile is the cross-device source of truth, defaults included: resetting on
  // another device has to propagate, not be masked by a stale local copy.
  const serverTheme = serverAppearance?.theme
  const serverAccent = serverAppearance?.accentColor
  const serverHighlight = serverAppearance?.highlightColor
  const loaded = serverAppearance !== undefined
  useEffect(() => {
    if (!loaded) return
    const next = normalizeAppearance({ theme: serverTheme, accent: serverAccent, highlight: serverHighlight })
    setState(next)
    cacheAppearance(next)
  }, [loaded, serverTheme, serverAccent, serverHighlight])

  useEffect(() => applyAppearance(appearance), [appearance])

  // A color picker reports every step of a drag: the page follows live, the save waits for a pause.
  const saveTimer = useRef<number | undefined>(undefined)
  const setAppearance = useCallback((a: Appearance) => {
    setState(a)
    cacheAppearance(a)
    window.clearTimeout(saveTimer.current)
    saveTimer.current = window.setTimeout(() => saveAppearance(a).catch(() => {}), 500)
  }, [])

  return <AppearanceContext.Provider value={{ appearance, setAppearance }}>{children}</AppearanceContext.Provider>
}
