import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { CalendarDto } from '../api/calendars'
import {
  createTask,
  deleteTask,
  listTasks,
  setTaskDone,
  type SaveTaskRequest,
  type TaskDto,
  type TaskPriority,
} from '../api/tasks'
import DateTimeField from './DateTimeField'
import { FormIcon } from './FormIcons'
import { allDayDate, dayKey, toLocalInput } from '../lib/dates'
import { dueToApi, isDone } from '../lib/tasks'

const PRIORITIES: { value: TaskPriority | null; label: string }[] = [
  { value: null, label: 'None' },
  { value: 'low', label: 'Low' },
  { value: 'normal', label: 'Normal' },
  { value: 'high', label: 'High' },
]

/** The editor's due field value ("YYYY-MM-DD" or "YYYY-MM-DDTHH:mm", local) for a task. */
function dueField(t: TaskDto | null): { value: string; allDay: boolean } | null {
  if (!t?.due) return null
  return t.dueAllDay ? { value: dayKey(allDayDate(t.due)), allDay: true } : { value: toLocalInput(new Date(t.due)), allDay: false }
}


/**
 * Edits one task — or creates one (task null). Title, notes, due date (with or without a time),
 * priority, calendar; for a top-level task also its checklist of subtasks, which save at once.
 */
export default function TaskEditor({
  task,
  calendars,
  defaultCalendarId,
  onSave,
  onDelete,
  onClose,
}: {
  task: TaskDto | null
  /** The calendars a task can live in (the user's own, not subscriptions). */
  calendars: CalendarDto[]
  defaultCalendarId?: string
  onSave: (body: SaveTaskRequest) => void
  /** Deletes the task (and its subtasks); absent for a new one. */
  onDelete?: () => void
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [title, setTitle] = useState(task?.title ?? '')
  const [notes, setNotes] = useState(task?.notes ?? '')
  const initialDue = dueField(task)
  const [due, setDue] = useState<string | null>(initialDue?.value ?? null)
  const [allDay, setAllDay] = useState(initialDue?.allDay ?? true)
  const [priority, setPriority] = useState<TaskPriority | null>((task?.priority as TaskPriority | null) ?? null)
  const [calendarId, setCalendarId] = useState(task?.calendarId ?? defaultCalendarId ?? calendars[0]?.id ?? '')
  const [newSub, setNewSub] = useState('')
  const isSubtask = !!task?.parentId
  // Its checklist, finished items included.
  const { data: all = [] } = useQuery({ queryKey: ['tasks', true], queryFn: () => listTasks(true), enabled: !!task && !isSubtask })
  const subtasks = all.filter((t) => t.parentId === task?.id).sort((a, b) => Number(a.sortOrder) - Number(b.sortOrder))

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['tasks'] })
  const addSub = useMutation({
    mutationFn: (t: string) => createTask({ title: t, parentId: task!.id }),
    onSuccess: () => { setNewSub(''); refresh() },
  })
  const tickSub = useMutation({ mutationFn: (s: TaskDto) => setTaskDone(s.id, !isDone(s)), onSuccess: refresh })
  const removeSub = useMutation({ mutationFn: (id: string) => deleteTask(id), onSuccess: refresh })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!title.trim()) return
    onSave({
      title: title.trim(),
      notes: notes.trim() || null,
      due: due ? dueToApi(due, allDay) : null,
      dueAllDay: due ? allDay : false,
      timeZone: due && !allDay ? Intl.DateTimeFormat().resolvedOptions().timeZone : null,
      priority,
      calendarId: isSubtask ? null : calendarId,
      parentId: task?.parentId ?? null,
      sortOrder: task ? Number(task.sortOrder) : null,
    })
  }

  const toggleTime = (withTime: boolean) => {
    setAllDay(!withTime)
    if (due) setDue(withTime ? `${due.slice(0, 10)}T09:00` : due.slice(0, 10))
  }

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <form className="modal event-form task-editor" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit}>
        <div className="modal-head">
          <span className="eyebrow">{task ? (isSubtask ? 'Edit subtask' : 'Edit task') : 'New task'}</span>
          <button type="button" className="modal-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
        <input
          className="form-title"
          aria-label="Title"
          placeholder="What needs doing?"
          value={title}
          maxLength={500}
          autoFocus
          onChange={(e) => setTitle(e.target.value)}
        />

        <div className="form-row">
          <FormIcon name="clock" />
          <div className="form-row-body task-due">
            {due === null ? (
              <button type="button" className="form-menu-btn is-quiet" onClick={() => setDue(dayKey(new Date()))}>
                Add a due date
              </button>
            ) : (
              <>
                <DateTimeField value={due} onChange={setDue} allDay={allDay} ariaLabel="Due" />
                <label className="form-check">
                  <input type="checkbox" checked={!allDay} onChange={(e) => toggleTime(e.target.checked)} /> At a time
                </label>
                <button type="button" className="btn-ghost task-due-clear" onClick={() => setDue(null)}>
                  No date
                </button>
              </>
            )}
          </div>
        </div>

        <div className="form-row">
          <span className="form-icon task-priority-icon" aria-hidden="true">!</span>
          <div className="form-row-body task-priority" role="radiogroup" aria-label="Priority">
            {PRIORITIES.map((p) => (
              <button
                key={p.label}
                type="button"
                role="radio"
                aria-checked={priority === p.value}
                className={'scope-option' + (priority === p.value ? ' active' : '')}
                onClick={() => setPriority(p.value)}
              >
                {p.label}
              </button>
            ))}
          </div>
        </div>

        {!isSubtask && calendars.length > 1 && (
          <div className="form-row">
            <FormIcon name="text" />
            <select className="form-select" aria-label="Calendar" value={calendarId} onChange={(e) => setCalendarId(e.target.value)}>
              {calendars.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className="form-row form-row-top">
          <FormIcon name="text" />
          <textarea
            className="form-row-body form-textarea"
            aria-label="Notes"
            rows={3}
            value={notes}
            maxLength={8000}
            placeholder="Add notes"
            onChange={(e) => setNotes(e.target.value)}
          />
        </div>

        {task && !isSubtask && (
          <div className="task-subtasks">
            <span className="repeat-label">Subtasks</span>
            {subtasks.map((s) => (
              <div key={s.id} className={'task-sub' + (isDone(s) ? ' is-done' : '')}>
                <button
                  type="button"
                  className={'task-check' + (isDone(s) ? ' is-done' : '')}
                  aria-label={isDone(s) ? `Open “${s.title}” again` : `Tick off “${s.title}”`}
                  onClick={() => tickSub.mutate(s)}
                />
                <span className="task-sub-title">{s.title}</span>
                <button type="button" className="cal-item-action danger" aria-label={`Delete “${s.title}”`} onClick={() => removeSub.mutate(s.id)}>
                  ✕
                </button>
              </div>
            ))}
            <input
              className="form-bare-input task-sub-add"
              placeholder="＋ Add a subtask"
              aria-label="New subtask"
              value={newSub}
              maxLength={500}
              onChange={(e) => setNewSub(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault() // a subtask, not the form
                  if (newSub.trim()) addSub.mutate(newSub.trim())
                }
              }}
            />
          </div>
        )}

        <div className="modal-actions">
          {task && onDelete && (
            <button type="button" className="btn-danger" onClick={onDelete}>
              Delete
            </button>
          )}
          <span className="spacer" />
          <button type="button" className="btn-ghost" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="btn-primary" disabled={!title.trim()}>
            Save
          </button>
        </div>
      </form>
    </div>
  )
}
