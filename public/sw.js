/* CalendarIT service worker — shows reminder notifications pushed by the backend.
 *
 * The backend sends a JSON payload { title, body, url, tag }. We show it as a notification;
 * clicking focuses an already-open CalendarIT tab (or opens one) at `url`.
 * Served from the site root so its scope covers the whole app. */

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
  event.waitUntil(self.registration.showNotification(title, options))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
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
