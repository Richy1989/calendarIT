/** Saves an .ics export through the browser's download. */
export function downloadIcs(blob: Blob, name = 'calendarit.ics'): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  URL.revokeObjectURL(url)
}
