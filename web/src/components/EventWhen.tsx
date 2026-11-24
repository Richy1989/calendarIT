import DateTimeField from './DateTimeField'
import { FormIcon } from './FormIcons'

/**
 * The time row of the event forms. An event on one day reads as one line —
 * "Tue, Nov 24 10:00 – 11:00", the end showing only its time (its picker still offers the date,
 * for an event that runs past midnight). An all-day event is two dates on one line. A timed event
 * over several days gets a line per end, "from" and "until", the fields aligned. "All day" sits below.
 */
export default function EventWhen({
  start,
  end,
  allDay,
  onStart,
  onEnd,
  onAllDay,
  startId,
  endId,
}: {
  start: string
  end: string
  allDay: boolean
  onStart: (value: string) => void
  onEnd: (value: string) => void
  onAllDay: (allDay: boolean) => void
  startId?: string
  endId?: string
}) {
  const sameDay = start.slice(0, 10) === end.slice(0, 10)
  const startField = <DateTimeField id={startId} ariaLabel="Starts" value={start} allDay={allDay} onChange={onStart} />
  const endField = (
    <DateTimeField id={endId} ariaLabel="Ends" value={end} allDay={allDay} onChange={onEnd} timeOnly={sameDay} />
  )

  return (
    <div className="form-row form-row-top">
      <FormIcon name="clock" />
      <div className="form-row-body form-when">
        {allDay || sameDay ? (
          <div className="form-when-line">
            {startField}
            <span className="form-when-sep" aria-hidden="true">–</span>
            {endField}
          </div>
        ) : (
          <>
            <div className="form-when-line">
              <span className="form-when-until">from</span>
              {startField}
            </div>
            <div className="form-when-line">
              <span className="form-when-until">until</span>
              {endField}
            </div>
          </>
        )}
        <label className="form-check">
          <input type="checkbox" checked={allDay} onChange={(e) => onAllDay(e.target.checked)} />
          All day
        </label>
      </div>
    </div>
  )
}
