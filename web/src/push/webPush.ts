import { api } from '../api/client'

/* Browser Web Push: register the service worker, ask permission, and hand the resulting
 * subscription to the backend so the reminder job can deliver notifications to this browser.
 * Web Push needs a secure context (https or localhost) — hence the `isPushSupported` guard. */

export type PushEnableResult = 'subscribed' | 'denied' | 'unsupported' | 'error'

export type NotifyMode = 'push' | 'local'

/** Pure decision: given permission + push capability, which mode this device lands in. */
export function decideNotifyMode(opts: {
  permission: NotificationPermission
  pushSupported: boolean
  pushSubscribed: boolean
}): NotifyMode | 'denied' {
  if (opts.permission !== 'granted') return 'denied'
  if (opts.pushSupported && opts.pushSubscribed) return 'push'
  return 'local'
}

const NOTIFY_MODE_KEY = 'calendarit.notifyMode'

/** The notification mode chosen on this browser, or null if notifications are off here. */
export function getNotifyMode(): NotifyMode | null {
  const v = localStorage.getItem(NOTIFY_MODE_KEY)
  return v === 'push' || v === 'local' ? v : null
}

function setNotifyMode(mode: NotifyMode): void {
  localStorage.setItem(NOTIFY_MODE_KEY, mode)
}

function clearNotifyMode(): void {
  localStorage.removeItem(NOTIFY_MODE_KEY)
}

/** Whether this browser can show local notifications (service worker + Notification API). */
export function isLocalNotifySupported(): boolean {
  return typeof window !== 'undefined' && 'serviceWorker' in navigator && 'Notification' in window
}

/** Whether this browser can do Web Push at all (Safari <16, http origins, etc. can't). */
export function isPushSupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    'serviceWorker' in navigator &&
    'PushManager' in window &&
    'Notification' in window
  )
}

/** The current Notification permission, or 'unsupported' when the API is absent. */
export function pushPermission(): NotificationPermission | 'unsupported' {
  return 'Notification' in window ? Notification.permission : 'unsupported'
}

async function registerServiceWorker(): Promise<ServiceWorkerRegistration> {
  const existing = await navigator.serviceWorker.getRegistration('/sw.js')
  return existing ?? (await navigator.serviceWorker.register('/sw.js'))
}

/**
 * Ensure this browser is subscribed to push for the signed-in user. Requests permission if
 * needed and registers the subscription with the backend. Idempotent: safe to call again — it
 * re-registers the existing subscription. Returns 'denied' if the user blocked notifications.
 */
export async function ensurePushSubscribed(): Promise<PushEnableResult> {
  if (!isPushSupported()) return 'unsupported'

  try {
    const permission = await Notification.requestPermission()
    if (permission !== 'granted') return 'denied'

    const registration = await registerServiceWorker()
    await navigator.serviceWorker.ready

    const { publicKey } = await fetchPublicKey()
    if (!publicKey) return 'error'

    const existing = await registration.pushManager.getSubscription()
    const subscription =
      existing ??
      (await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(publicKey),
      }))

    const json = subscription.toJSON()
    if (!json.endpoint || !json.keys?.p256dh || !json.keys?.auth) return 'error'

    const { error } = await api.POST('/api/push/subscribe', {
      body: { endpoint: json.endpoint, keys: { p256dh: json.keys.p256dh, auth: json.keys.auth } },
    })
    if (error) return 'error'
    return 'subscribed'
  } catch (err) {
    console.error('Failed to enable browser notifications', err)
    return 'error'
  }
}

/** Turn off push for this browser: drop the local subscription and tell the backend to forget it. */
export async function disablePush(): Promise<void> {
  if (!isPushSupported()) return
  const registration = await navigator.serviceWorker.getRegistration('/sw.js')
  const subscription = await registration?.pushManager.getSubscription()
  if (!subscription) return
  const endpoint = subscription.endpoint
  await subscription.unsubscribe()
  await api.POST('/api/push/unsubscribe', { body: { endpoint } })
}

async function fetchPublicKey(): Promise<{ publicKey: string | null }> {
  const { data } = await api.GET('/api/push/public-key')
  return { publicKey: data?.publicKey ?? null }
}

/** VAPID keys travel as URL-safe base64; the Push API wants the raw bytes. */
function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4)
  const normalized = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/')
  const raw = window.atob(normalized)
  const output = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) output[i] = raw.charCodeAt(i)
  return output
}

/**
 * Enable notifications on this device. Tries real Web Push first (delivers when the app is closed);
 * if the browser can't/won't subscribe but notification permission is granted, falls back to local
 * mode (the app polls and shows notifications while open). Persists the resolved mode.
 */
export async function enableNotifications(): Promise<NotifyMode | 'denied' | 'unsupported'> {
  if (!isLocalNotifySupported()) return 'unsupported'

  const permission = await Notification.requestPermission()
  if (permission !== 'granted') return 'denied'

  let pushSubscribed = false
  if (isPushSupported()) {
    pushSubscribed = (await ensurePushSubscribed()) === 'subscribed'
  }

  const mode = decideNotifyMode({ permission, pushSupported: isPushSupported(), pushSubscribed })
  if (mode === 'denied') return 'denied'

  if (mode === 'local') {
    // No push subscription, but we still need the service worker registered to show notifications.
    await registerServiceWorker()
  }
  setNotifyMode(mode)
  return mode
}

/** Turn notifications off on this device: drop any push subscription and clear the stored mode. */
export async function disableNotifications(): Promise<void> {
  clearNotifyMode()
  await disablePush()
}
