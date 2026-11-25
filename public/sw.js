/* 24seven service worker — shows reminder notifications pushed by the backend.
 *
 * The backend sends a JSON payload { title, body, url, tag, snoozeToken }. We show it as a
 * notification; clicking focuses an already-open 24seven tab (or opens one) at `url`. With a
 * snoozeToken it gets Snooze / Dismiss buttons: Snooze posts the token back, and the server shows
 * the reminder again in 10 minutes (this worker has no sign-in, and can't keep a timer running).
 * Served from the site root so its scope covers the whole app. */

const SNOOZE_MINUTES = 10

/** Notification options for a reminder: its buttons, and the token Snooze needs. */
function reminderOptions(options, snoozeToken) {
  if (!snoozeToken) return options
  return {
    ...options,
    data: { ...options.data, snoozeToken },
    actions: [
      { action: 'snooze', title: `Snooze ${SNOOZE_MINUTES} min` },
      { action: 'dismiss', title: 'Dismiss' },
    ],
  }
}

// The page shows its own polled reminders through here too, so they get the same buttons.
self.addEventListener('message', (event) => {
  const msg = event.data
  if (!msg || msg.type !== 'show-reminder') return
  event.waitUntil(
    self.registration.showNotification(msg.title, reminderOptions(msg.options || {}, msg.snoozeToken)),
  )
})

self.addEventListener('push', (event) => {
  let payload = {}
  try {
    payload = event.data ? event.data.json() : {}
  } catch {
    payload = { title: 'Reminder', body: event.data ? event.data.text() : '' }
  }

  const title = payload.title || 'Reminder'
  const options = {
    body: payload.body || '',
    tag: payload.tag || undefined,
    // Reminders shouldn't silently vanish before the user glances at them.
    requireInteraction: false,
    data: { url: payload.url || '/' },
  }
  event.waitUntil(self.registration.showNotification(title, reminderOptions(options, payload.snoozeToken)))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  if (event.action === 'dismiss') return
  if (event.action === 'snooze') {
    const token = event.notification.data && event.notification.data.snoozeToken
    if (!token) return
    event.waitUntil(
      fetch('/api/reminders/snooze', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token, minutes: SNOOZE_MINUTES }),
      }).catch(() => {}),
    )
    return
  }
  const url = (event.notification.data && event.notification.data.url) || '/'
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
      for (const client of clients) {
        if ('focus' in client) return client.focus()
      }
      return self.clients.openWindow(url)
    }),
  )
})
