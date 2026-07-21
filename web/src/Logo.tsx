/** CalendarIT brand mark: a time-grid with one cell lit by the signature spectrum. */
export default function Logo({ className = 'brand-mark' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 32 32" fill="none" aria-hidden="true">
      <defs>
        <linearGradient id="calit-grad" x1="2" y1="2" x2="30" y2="30" gradientUnits="userSpaceOnUse">
          <stop stopColor="#7C6BFF" />
          <stop offset="0.52" stopColor="#5B8DEF" />
          <stop offset="1" stopColor="#35E0D4" />
        </linearGradient>
      </defs>
      <rect x="3.5" y="5.5" width="25" height="23" rx="6" stroke="url(#calit-grad)" strokeWidth="2" />
      <path d="M3.5 12H28.5" stroke="url(#calit-grad)" strokeWidth="2" />
      <path d="M11 3V8M21 3V8" stroke="url(#calit-grad)" strokeWidth="2" strokeLinecap="round" />
      <rect x="14.4" y="16.4" width="6.2" height="6.2" rx="1.7" fill="url(#calit-grad)" />
    </svg>
  )
}
