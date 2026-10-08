/**
 * A short, human name for the device behind a User-Agent ("Firefox on Linux"), for telling
 * sessions apart. Only a label — never used for anything that matters — so a loose match that is
 * right for the browsers people actually use beats a full UA parser.
 */
export function describeDevice(userAgent?: string | null): string {
  const ua = userAgent?.trim()
  if (!ua) return 'Unknown device'
  const browser = /Edg(e|A|iOS)?\//.test(ua)
    ? 'Edge'
    : /OPR\/|Opera/.test(ua)
      ? 'Opera'
      : /Firefox\/|FxiOS\//.test(ua)
        ? 'Firefox'
        : /Chrome\/|CriOS\//.test(ua)
          ? 'Chrome'
          : /Safari\//.test(ua)
            ? 'Safari'
            : null
  const os = /iPhone|iPad|iPod/.test(ua)
    ? 'iOS'
    : /Android/.test(ua)
      ? 'Android'
      : /Windows/.test(ua)
        ? 'Windows'
        : /Macintosh|Mac OS X/.test(ua)
          ? 'macOS'
          : /CrOS/.test(ua)
            ? 'ChromeOS'
            : /Linux/.test(ua)
              ? 'Linux'
              : null
  if (browser && os) return `${browser} on ${os}`
  return browser ?? os ?? (ua.length > 40 ? `${ua.slice(0, 40)}…` : ua)
}
