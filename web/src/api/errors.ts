/**
 * The message worth showing for a failed API call. The server answers in three shapes: RFC 7807
 * problems (`detail`), validation problems (`errors: { field: [messages] }`), and the auth
 * endpoints' `{ errors: string[] }`. Whatever it said, the first concrete reason wins; anything
 * unrecognisable falls back to `fallback`, which should already read well on its own.
 */
export function problemMessage(body: unknown, fallback: string): string {
  if (!body || typeof body !== 'object') return fallback
  const b = body as { detail?: unknown; errors?: unknown; title?: unknown }
  if (typeof b.detail === 'string' && b.detail.trim()) return b.detail
  if (Array.isArray(b.errors) && b.errors.length > 0) return String(b.errors[0])
  if (b.errors && typeof b.errors === 'object') {
    for (const messages of Object.values(b.errors as Record<string, unknown>)) {
      if (Array.isArray(messages) && messages.length > 0) return String(messages[0])
    }
  }
  return fallback
}
