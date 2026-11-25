import { describe, expect, it } from 'vitest'
import { inkOn, normalizeAppearance, THEMES } from './appearance'

describe('normalizeAppearance', () => {
  it('keeps a known theme and well-formed colors, lowercased', () => {
    expect(normalizeAppearance({ theme: 'graphite', accent: '#F0A45A', highlight: '#ff8a6b' })).toEqual({
      theme: 'graphite', accent: '#f0a45a', highlight: '#ff8a6b',
    })
  })

  it('falls back to Corner Store and the theme colors for anything else', () => {
    expect(normalizeAppearance({ theme: 'neon', accent: 'red', highlight: '#12345' })).toEqual({
      theme: 'corner-store', accent: null, highlight: null,
    })
    expect(normalizeAppearance(null)).toEqual({ theme: 'corner-store', accent: null, highlight: null })
    // Midnight, once the default, is now a theme like the others.
    expect(normalizeAppearance({ theme: 'midnight' }).theme).toBe('midnight')
    // A value that would break out of the CSS property never gets through.
    expect(normalizeAppearance({ accent: '#123456;background:url(x)' }).accent).toBeNull()
  })
})

describe('inkOn', () => {
  it('puts dark text on light colors and white on dark ones', () => {
    expect(inkOn('#f0a45a')).toBe('#0b0f18') // amber
    expect(inkOn('#35e0d4')).toBe('#0b0f18') // cyan
    expect(inkOn('#4a63e0')).toBe('#ffffff') // deep blue
    expect(inkOn('#1f2937')).toBe('#ffffff')
  })
})

describe('THEMES', () => {
  it('offers three distinct themes', () => {
    expect(THEMES.map((t) => t.id)).toEqual(['corner-store', 'midnight', 'daylight', 'graphite'])
  })
})
