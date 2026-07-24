import type { ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useDismiss } from '../lib/useDismiss'

/** Where to anchor a popover — the viewport coordinates of its top-left corner. */
export type Anchor = { x: number; y: number }

/** Anchors a popover under an element's bounding rect, nudged down by a small gap. */
export function anchorBelow(el: Element, gap = 6): Anchor {
  const r = el.getBoundingClientRect()
  return { x: r.left, y: r.bottom + gap }
}

/**
 * A floating layer anchored at fixed viewport coordinates, over a full-screen backdrop that
 * dismisses on outside click (or right-click). Escape / scroll / resize also dismiss it.
 * Rendered only while open, so mount it conditionally: {open && <Popover …/>}. This is the
 * one popover primitive the whole app shares — calendar/category switchers, the year jump,
 * and the date-time picker all build on it.
 *
 * It renders through a portal to document.body so it's never clipped or offset by an ancestor's
 * overflow/transform/filter (e.g. the event modal, which is overflow-hidden and sits under a
 * backdrop-filtered overlay). React events still bubble along the component tree, so a parent's
 * click-guard (like the modal's stop-propagation) keeps working across the portal.
 */
export function Popover({
  anchor,
  onClose,
  className,
  children,
}: {
  anchor: Anchor
  onClose: () => void
  /** Class for the floating panel itself (its look; positioning is handled here). */
  className?: string
  children: ReactNode
}) {
  useDismiss(true, onClose)
  return createPortal(
    <div
      className="ctx-backdrop"
      onMouseDown={onClose}
      onContextMenu={(e) => {
        e.preventDefault()
        onClose()
      }}
    >
      <div
        className={className}
        style={{ position: 'fixed', left: anchor.x, top: anchor.y }}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {children}
      </div>
    </div>,
    document.body,
  )
}
