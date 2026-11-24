// The line icons the event forms label their rows with (in place of a text label, which each
// row still carries for screen readers). Stroke-only, sized and colored from CSS (.form-icon).

const PATHS = {
  clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
  repeat: <><path d="M17 2l4 4-4 4" /><path d="M3 11V9a3 3 0 0 1 3-3h15" /><path d="M7 22l-4-4 4-4" /><path d="M21 13v2a3 3 0 0 1-3 3H3" /></>,
  users: <><circle cx="9" cy="8" r="3.5" /><path d="M2.5 20a6.5 6.5 0 0 1 13 0" /><path d="M16 4.5a3.5 3.5 0 0 1 0 7" /><path d="M18 14.5a6.5 6.5 0 0 1 3.5 5.5" /></>,
  pin: <><path d="M12 21s-7-6.2-7-11.5a7 7 0 0 1 14 0C19 14.8 12 21 12 21z" /><circle cx="12" cy="9.5" r="2.5" /></>,
  text: <><path d="M4 6h16" /><path d="M4 12h16" /><path d="M4 18h10" /></>,
  bell: <><path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4z" /><path d="M10 20a2 2 0 0 0 4 0" /></>,
  close: <><path d="M6 6l12 12" /><path d="M18 6L6 18" /></>,
  grip: <><circle cx="9" cy="6" r="1" /><circle cx="15" cy="6" r="1" /><circle cx="9" cy="12" r="1" /><circle cx="15" cy="12" r="1" /><circle cx="9" cy="18" r="1" /><circle cx="15" cy="18" r="1" /></>,
  chevron: <path d="M6 9l6 6 6-6" />,
} as const

export type FormIconName = keyof typeof PATHS

export function FormIcon({ name, className }: { name: FormIconName; className?: string }) {
  return (
    <svg className={'form-icon' + (className ? ` ${className}` : '')} viewBox="0 0 24 24" aria-hidden="true">
      {PATHS[name]}
    </svg>
  )
}

/** The calendar/category row's marker: a dot in the color the event will show in. */
export function ColorDot({ color }: { color: string }) {
  return (
    <span className="form-icon form-dot" aria-hidden="true">
      <span style={{ background: color }} />
    </span>
  )
}
