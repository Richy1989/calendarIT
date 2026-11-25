import { createContext, useContext } from 'react'

// The app's look: one of four themes plus the user's own two main colors over it. Like the
// clock and week start it's a server-side profile preference (so a phone and a laptop agree),
// cached in localStorage and applied before React renders, so the first paint is already right —
// a dark page flashing white on load (or the other way round) is worse than either theme.
//
// The themes live in index.css (`:root[data-theme=…]`); this sets the attribute and, when the
// user picked their own, the --accent / --highlight custom properties on <html>.

const KEY = 'calendarit.appearance'

export type ThemeId = 'corner-store' | 'midnight' | 'daylight' | 'graphite'

/** The look of a user who never picked one (`:root` in index.css). */
export const DEFAULT_THEME: ThemeId = 'corner-store'

export type Appearance = {
  theme: ThemeId
  /** '#rrggbb', or null for the theme's own. */
  accent: string | null
  highlight: string | null
}

/** The themes, with the values the Settings previews draw (they mirror index.css). */
export const THEMES: {
  id: ThemeId
  name: string
  description: string
  bg: string
  surface: string
  hairline: string
  text: string
  textDim: string
  accent: string
  highlight: string
}[] = [
  {
    id: 'corner-store', name: 'Corner Store', description: 'Warm ink, cream and coral',
    bg: '#141110', surface: '#26201c', hairline: '#372f29', text: '#fff0d7', textDim: '#8f8070', accent: '#ff694e', highlight: '#ffb547',
  },
  {
    id: 'midnight', name: 'Midnight', description: 'Deep ink, violet to cyan',
    bg: '#0a0d14', surface: '#171d2b', hairline: '#232c3e', text: '#e7ecf5', textDim: '#62708a', accent: '#6e8bff', highlight: '#35e0d4',
  },
  {
    id: 'daylight', name: 'Daylight', description: 'Light and airy',
    bg: '#f4f6fb', surface: '#ffffff', hairline: '#dde2ec', text: '#172033', textDim: '#636f87', accent: '#4a63e0', highlight: '#0b8f86',
  },
  {
    id: 'graphite', name: 'Graphite', description: 'Charcoal, amber and coral',
    bg: '#111112', surface: '#222225', hairline: '#2f2f34', text: '#ededee', textDim: '#7a7a83', accent: '#f0a45a', highlight: '#ff8a6b',
  },
]

/** Swatches offered for both colors (any other is one click away in the color picker). */
export const COLOR_PRESETS = [
  '#6e8bff', '#4a63e0', '#8b5cf6', '#d946ef', '#ec4899', '#ef4444',
  '#f97316', '#f0a45a', '#eab308', '#22c55e', '#14b8a6', '#35e0d4',
]

export const DEFAULT_APPEARANCE: Appearance = { theme: DEFAULT_THEME, accent: null, highlight: null }

const HEX = /^#[0-9a-f]{6}$/i
const isTheme = (v: unknown): v is ThemeId => THEMES.some((t) => t.id === v)
const color = (v: unknown) => (typeof v === 'string' && HEX.test(v) ? v.toLowerCase() : null)

export function normalizeAppearance(raw: { theme?: unknown; accent?: unknown; highlight?: unknown } | null | undefined): Appearance {
  return {
    theme: isTheme(raw?.theme) ? raw.theme : DEFAULT_THEME,
    accent: color(raw?.accent),
    highlight: color(raw?.highlight),
  }
}

export function getCachedAppearance(): Appearance {
  try {
    return normalizeAppearance(JSON.parse(localStorage.getItem(KEY) ?? 'null'))
  } catch {
    return DEFAULT_APPEARANCE
  }
}

export function cacheAppearance(a: Appearance) {
  localStorage.setItem(KEY, JSON.stringify(a))
}

/** WCAG relative luminance of '#rrggbb'. */
function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

/** Text on the accent gradient: dark on a light accent, white on a dark one. */
export function inkOn(hex: string): string {
  const l = luminance(hex)
  return (l + 0.05) / 0.05 >= 1.05 / (l + 0.05) ? '#0b0f18' : '#ffffff'
}

/** Puts an appearance on the page: the theme attribute, and the user's colors when set. */
export function applyAppearance(a: Appearance) {
  const root = document.documentElement
  if (a.theme === DEFAULT_THEME) delete root.dataset.theme
  else root.dataset.theme = a.theme
  const set = (prop: string, value: string | null) =>
    value ? root.style.setProperty(prop, value) : root.style.removeProperty(prop)
  set('--accent', a.accent)
  set('--highlight', a.highlight)
  // The theme's own text-on-accent fits its own accent; a chosen accent gets one that fits it.
  set('--on-accent', a.accent ? inkOn(a.accent) : null)
  // The browser's own UI (address bar on phones) follows the page.
  const theme = THEMES.find((t) => t.id === a.theme)!
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme.bg)
}

export type ThemeCtx = { appearance: Appearance; setAppearance: (a: Appearance) => void }
export const AppearanceContext = createContext<ThemeCtx>({ appearance: DEFAULT_APPEARANCE, setAppearance: () => {} })

export function useAppearance(): ThemeCtx {
  return useContext(AppearanceContext)
}
