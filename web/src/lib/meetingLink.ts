// Finds the video-call link in an appointment's location or description (pasted from Zoom,
// Teams, Meet, …) so the event can offer a one-click "Join". Only https links to the services
// below count: the text is often an invitation someone else wrote, and the button must never
// become a way to make an arbitrary link look like the meeting.

export type MeetingLink = { url: string; service: string }

const SERVICES: { service: string; host: RegExp; path: RegExp }[] = [
  { service: 'Zoom', host: /(^|\.)zoom\.us$/, path: /^\/(j|my|w|s)\/[^/]+/ },
  { service: 'Teams', host: /^teams\.microsoft\.com$/, path: /^\/(l\/meetup-join|meet)\// },
  { service: 'Teams', host: /^teams\.live\.com$/, path: /^\/meet\// },
  { service: 'Google Meet', host: /^meet\.google\.com$/, path: /^\/[a-z]{3,4}-[a-z]{3,4}-[a-z]{3,4}/ },
  { service: 'Jitsi', host: /^meet\.jit\.si$/, path: /^\/[^/]+/ },
  { service: 'Webex', host: /(^|\.)webex\.com$/, path: /^\/./ },
  { service: 'Whereby', host: /^whereby\.com$/, path: /^\/[^/]+/ },
]

// Up to the first character that can't be part of a pasted link.
const URL_PATTERN = /https:\/\/[^\s<>"'()]+/gi

/** The first meeting link in the given texts (location first, then description), or null. */
export function findMeetingLink(...texts: (string | null | undefined)[]): MeetingLink | null {
  for (const text of texts) {
    if (!text) continue
    for (const match of text.matchAll(URL_PATTERN)) {
      // Trailing punctuation belongs to the sentence, not the link ("…/j/123.").
      const candidate = match[0].replace(/[.,;:!?\]]+$/, '')
      let url: URL
      try {
        url = new URL(candidate)
      } catch {
        continue
      }
      if (url.protocol !== 'https:' || url.username || url.password) continue
      const host = url.hostname.toLowerCase()
      const known = SERVICES.find((s) => s.host.test(host) && s.path.test(url.pathname))
      if (known) return { url: url.href, service: known.service }
    }
  }
  return null
}
