/** The 24seven app icon (public/logo.svg): "24 SEVEN" in cream on the corner store's coral. */
export default function Logo({ className = 'brand-mark' }: { className?: string }) {
  return <img className={className} src="/logo.svg" alt="" aria-hidden="true" draggable={false} />
}
