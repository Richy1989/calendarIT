import { useCallback, useRef } from 'react'

/**
 * One reversible thing the user did. `undo` puts the world back the way it was; `redo`
 * re-applies the change. Both may talk to the server, so they return promises. `label` is
 * shown in the "Undid: …" / "Redid: …" toast so a step through the stack isn't blind.
 *
 * Closures capture whatever they need (request bodies, ids) so an entry is self-contained
 * and doesn't depend on what's currently on screen. An entry whose identity changes across
 * runs (e.g. a delete/recreate that mints a new id) keeps that id in its own closure scope.
 */
export type UndoAction = {
  label: string
  // Resolved values are ignored (the closures often return the mutation's DTO) — only the
  // completion matters, so `step` just awaits them.
  undo: () => void | Promise<unknown>
  redo: () => void | Promise<unknown>
}

/** How many steps back Ctrl-Z reaches. Older entries fall off the bottom. */
const LIMIT = 10

/**
 * An in-memory undo/redo stack (resets on reload — replaying a recorded mutation against a
 * since-reloaded server is where undo goes wrong, so it deliberately doesn't persist).
 *
 * `record` pushes a new action and clears the redo stack (a fresh change forks history, as
 * in any editor). `undo`/`redo` run one step, moving the entry to the other stack, and
 * return its label (or null when there's nothing to do). A single flight is enforced so a
 * burst of Ctrl-Z presses can't race; a step that throws is dropped rather than left dangling.
 */
export function useUndoStack() {
  const undoStack = useRef<UndoAction[]>([])
  const redoStack = useRef<UndoAction[]>([])
  const busy = useRef(false)

  const record = useCallback((action: UndoAction) => {
    undoStack.current.push(action)
    if (undoStack.current.length > LIMIT) undoStack.current.shift()
    redoStack.current = []
  }, [])

  const step = useCallback(
    async (from: React.RefObject<UndoAction[]>, to: React.RefObject<UndoAction[]>, run: (a: UndoAction) => void | Promise<unknown>) => {
      if (busy.current) return null
      const action = from.current.pop()
      if (!action) return null
      busy.current = true
      try {
        await run(action) // if this throws, the entry is gone — we don't re-queue a broken step
        to.current.push(action)
        if (to.current.length > LIMIT) to.current.shift()
        return action.label
      } finally {
        busy.current = false
      }
    },
    [],
  )

  const undo = useCallback(() => step(undoStack, redoStack, (a) => a.undo()), [step])
  const redo = useCallback(() => step(redoStack, undoStack, (a) => a.redo()), [step])

  return { record, undo, redo }
}
