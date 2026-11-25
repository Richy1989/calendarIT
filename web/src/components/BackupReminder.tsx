import { useState } from 'react'
import { exportIcs } from '../api/events'
import { downloadIcs } from '../lib/download'
import { isBackupDue, markBackedUp, snoozeBackupReminder } from '../lib/backupReminder'

/** A slim bar, every 30 days: export everything now (one click), or be asked again in a week. */
export default function BackupReminder({ userKey }: { userKey: string }) {
  const [open, setOpen] = useState(() => isBackupDue(userKey))
  const [failed, setFailed] = useState(false)
  if (!open) return null

  const exportNow = async () => {
    try {
      downloadIcs(await exportIcs())
      markBackedUp(userKey)
      setOpen(false)
    } catch {
      setFailed(true)
    }
  }

  return (
    <div className="backup-bar" role="status">
      <span>{failed ? 'The export failed — try again, or from Settings → General.' : 'Time for a backup? Export all your calendars as an .ics file.'}</span>
      <button type="button" className="btn-primary" onClick={exportNow}>
        Export now
      </button>
      <button
        type="button"
        className="btn-ghost"
        onClick={() => {
          snoozeBackupReminder(userKey)
          setOpen(false)
        }}
      >
        Later
      </button>
    </div>
  )
}
