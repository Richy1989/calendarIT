// What the quick create form and the full editor share: the repeat presets (named after the
// event's own start), reminder wording, and the reminders a new event starts with.

import { weekdayLabel, weekdayOf } from './rrule'

export type ReminderValue = { minutesBefore: number; channel: string }

/** Repeat presets as written to the API. The full editor recognizes these rules as presets. */
export const REPEAT_RULES = {
  none: '',
  daily: 'FREQ=DAILY',
  weekdays: 'FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR',
  weekly: 'FREQ=WEEKLY',
  monthly: 'FREQ=MONTHLY',
  yearly: 'FREQ=YEARLY',
} as const

export type RepeatPreset = keyof typeof REPEAT_RULES

/** The presets with labels that name the start's own day ("Weekly on Wednesday"). */
export function repeatPresets(start: Date): { preset: RepeatPreset; rule: string; label: string }[] {
  const weekday = weekdayLabel(weekdayOf(start), 'long')
  const dayOfMonth = start.getDate()
  const yearly = start.toLocaleDateString(undefined, { day: 'numeric', month: 'long' })
  return [
    { preset: 'none', rule: REPEAT_RULES.none, label: 'Does not repeat' },
    { preset: 'daily', rule: REPEAT_RULES.daily, label: 'Every day' },
    { preset: 'weekdays', rule: REPEAT_RULES.weekdays, label: 'Every weekday (Mon–Fri)' },
    { preset: 'weekly', rule: REPEAT_RULES.weekly, label: `Weekly on ${weekday}` },
    { preset: 'monthly', rule: REPEAT_RULES.monthly, label: `Monthly on day ${dayOfMonth}` },
    { preset: 'yearly', rule: REPEAT_RULES.yearly, label: `Annually on ${yearly}` },
  ]
}

export const REMINDER_OFFSETS: { label: string; short: string; value: number }[] = [
  { label: 'At start of event', short: 'At start', value: 0 },
  { label: '5 minutes before', short: '5 min before', value: 5 },
  { label: '10 minutes before', short: '10 min before', value: 10 },
  { label: '15 minutes before', short: '15 min before', value: 15 },
  { label: '30 minutes before', short: '30 min before', value: 30 },
  { label: '1 hour before', short: '1 hour before', value: 60 },
  { label: '2 hours before', short: '2 hours before', value: 120 },
  { label: '1 day before', short: '1 day before', value: 1440 },
  { label: '1 week before', short: '1 week before', value: 10080 },
]

export const channelLabel = (channel: string) => (channel === 'WebPush' ? 'Browser' : 'Email')

/** "15 min before · Browser" — or a plain minute count for an offset no preset names. */
export function describeReminder(r: ReminderValue): string {
  const preset = REMINDER_OFFSETS.find((o) => o.value === r.minutesBefore)
  return `${preset?.short ?? `${r.minutesBefore} min before`} · ${channelLabel(r.channel)}`
}

/** The reminders a new event in `calendar` starts with: the calendar's defaults for a timed
 *  event, none for an all-day one (a minutes-before-midnight reminder isn't what anyone means). */
export function defaultRemindersFor(
  calendar: { defaultReminders?: { minutesBefore: number | string; channel: string }[] | null } | undefined,
  allDay: boolean,
): ReminderValue[] {
  if (allDay || !calendar?.defaultReminders) return []
  return calendar.defaultReminders.map((r) => ({ minutesBefore: Number(r.minutesBefore), channel: r.channel }))
}

export const sameReminders = (a: ReminderValue[], b: ReminderValue[]) =>
  a.length === b.length && a.every((r, i) => r.minutesBefore === b[i].minutesBefore && r.channel === b[i].channel)
