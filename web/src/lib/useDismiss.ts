import { useEffect } from 'react'

/**
 * While `active`, calls `onClose` on Escape, or when the page scrolls or resizes under a
 * floating layer (popovers/menus are anchored to a rect, so they'd drift — closing is the
 * expected behavior here, and matches the app's other popovers). Outside-click is handled by
 * the layer's own backdrop element, not this hook.
 */
export function useDismiss(active: boolean, onClose: () => void): void {
  useEffect(() => {
    if (!active) return
    const close = () => onClose()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
      window.removeEventListener('keydown', onKey)
    }
  }, [active, onClose])
}
