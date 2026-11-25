import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createTask, listTasks, setTaskDone, type TaskDto } from '../api/tasks'
import { useHour12 } from '../clock'
import { GROUP_LABELS, dueLabel, groupOf, groupTasks, isDone } from '../lib/tasks'

/**
 * The task list beside the calendar: a quick-add line, then open tasks grouped by when they are
 * due (overdue, today, upcoming, no date), each with its subtasks; finished ones on request.
 */
export default function TaskPanel({
  defaultCalendarId,
  visibleCalendarIds,
  onOpen,
  onClose,
}: {
  defaultCalendarId?: string
  /** Only tasks in these calendars (null = all). */
  visibleCalendarIds: string[] | null
  onOpen: (task: TaskDto) => void
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const hour12 = useHour12()
  const [showDone, setShowDone] = useState(false)
  const [draft, setDraft] = useState('')
  const { data: tasks = [], isLoading } = useQuery({ queryKey: ['tasks', showDone], queryFn: () => listTasks(showDone) })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['tasks'] })
  const [error, setError] = useState<string | null>(null)

  const add = useMutation({
    mutationFn: (title: string) => createTask({ title, calendarId: defaultCalendarId ?? null }),
    onSuccess: () => { setDraft(''); setError(null); refresh() },
    onError: (e) => setError((e as Error).message),
  })
  const tick = useMutation({
    mutationFn: (t: TaskDto) => setTaskDone(t.id, !isDone(t)),
    onSuccess: refresh,
    onError: (e) => setError((e as Error).message),
  })

  const shown = tasks.filter((t) => !visibleCalendarIds || visibleCalendarIds.includes(t.calendarId))
  const groups = groupTasks(shown)

  const row = (t: TaskDto, sub = false) => {
    const label = dueLabel(t, hour12)
    const overdue = groupOf(t) === 'overdue'
    return (
      <div key={t.id} className={'task-row' + (sub ? ' is-sub' : '') + (isDone(t) ? ' is-done' : '')}>
        <button
          type="button"
          className={'task-check' + (isDone(t) ? ' is-done' : '')}
          aria-label={isDone(t) ? `Open “${t.title}” again` : `Tick off “${t.title}”`}
          onClick={() => tick.mutate(t)}
        />
        <button type="button" className="task-row-main" onClick={() => onOpen(t)}>
          <span className="task-row-title">
            {t.priority === 'high' && <span className="task-prio is-high" title="High priority">!</span>}
            {t.title}
          </span>
          {label && !sub && <span className={'task-row-due' + (overdue ? ' is-overdue' : '')}>{label}</span>}
          {t.notes && <span className="task-row-notes">{t.notes}</span>}
        </button>
      </div>
    )
  }

  return (
    <aside className="task-panel" aria-label="Tasks">
      <div className="task-panel-head">
        <h2>Tasks</h2>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Hide tasks">
          ✕
        </button>
      </div>

      <form
        className="task-add"
        onSubmit={(e) => {
          e.preventDefault()
          if (draft.trim()) add.mutate(draft.trim())
        }}
      >
        <input
          value={draft}
          placeholder="＋ Add a task"
          aria-label="New task"
          maxLength={500}
          onChange={(e) => setDraft(e.target.value)}
        />
      </form>
      {error && <p className="error">{error}</p>}

      <div className="task-list">
        {isLoading && <p className="task-empty">Loading…</p>}
        {!isLoading && groups.length === 0 && <p className="task-empty">Nothing to do. Add a task above.</p>}
        {groups.map((g) => (
          <section key={g.id} className={'task-group is-' + g.id}>
            <h3>{GROUP_LABELS[g.id]}</h3>
            {g.nodes.map((node) => (
              <div key={node.task.id} className="task-node">
                {row(node.task)}
                {node.subtasks.map((s) => row(s, true))}
              </div>
            ))}
          </section>
        ))}
      </div>

      <button type="button" className="cal-add task-show-done" onClick={() => setShowDone((v) => !v)}>
        {showDone ? 'Hide finished tasks' : 'Show finished tasks'}
      </button>
    </aside>
  )
}
