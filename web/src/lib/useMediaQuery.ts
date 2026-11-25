import { useEffect, useState } from 'react'

/** Tracks a CSS media query and re-renders when it flips. */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => typeof window !== 'undefined' && window.matchMedia(query).matches)
  useEffect(() => {
    const mq = window.matchMedia(query)
    const onChange = () => setMatches(mq.matches)
    onChange()
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [query])
  return matches
}

/** Phone layout: the calendar's bars move around the grid, and the app keeps its own header. */
export const NARROW_QUERY = '(max-width: 640px)'
