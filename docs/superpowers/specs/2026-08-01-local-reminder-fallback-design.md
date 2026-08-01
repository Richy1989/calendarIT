# Local reminder notifications — a no-Google fallback for browser reminders

## Problem

CalendarIT reminders have two channels today: **Email** (sent through the user's own connected
mail account) and **WebPush** (the "Browser" option). Real Web Push depends on each browser's push
service — Google FCM for Chromium, Mozilla for Firefox, Apple for Safari. **Brave disables the
Google path by default**, and non-HTTPS origins / very old Safari can't subscribe at all. On those,
a reminder set to "Browser" silently never fires.

We want a fallback that delivers **real desktop/system notifications without Google**, working
across all modern browsers (Firefox and Chromium at minimum), with the accepted limitation that the
fallback only fires **while a CalendarIT tab is open or the app is installed as a PWA**. Background
delivery when the app is fully closed already has a no-Google answer: Email.

## Approach (chosen)

Reuse the existing `WebPush` ("Browser") channel. Each browser runs in exactly one of two modes:

- **Push mode** — a real push subscription exists; the backend `ReminderDispatchJob` delivers
  (works when the app is closed). Unchanged from today.
- **Local mode** — no push subscription could be created (Brave with FCM off, non-HTTPS, old
  Safari). The browser polls a new read-only endpoint while open and shows notifications itself.

A device is in exactly one mode, so it can never double-notify. No new reminder channel, no
database migration.

### Cross-browser behavior

| Browser | Path | Works when app closed? |
|---|---|---|
| Firefox | Native push (Mozilla) | Yes |
| Chrome / Edge | Native push (FCM) | Yes |
| Brave (FCM off) | Local-poll fallback | No (only while open) |
| Safari 16.4+ (installed PWA) | Native push (Apple) | Yes |
| Safari (not installed) / HTTP origin / push blocked but notifications allowed | Local-poll fallback | No (only while open) |

The local-poll path uses only universal APIs (`fetch`, `setInterval`,
`ServiceWorkerRegistration.showNotification`), so it behaves identically in every modern browser.

## Components

### 1. Backend — read-only "due reminders" endpoint

`GET /api/reminders/due?sinceUtc=<ISO-8601>` (authenticated; current user only).

- Returns the signed-in user's reminders **filtered to `Channel == WebPush`** whose trigger time
  (occurrence start − `MinutesBefore`) falls in `(sinceUtc, now]`.
- Recurrence-expanded and timezone-correct, reusing the same `RecurrenceExpander` logic and window
  arithmetic as `ReminderDispatchJob` (occurrence start in `(sinceUtc + offset, now + offset]`).
- `sinceUtc` is **clamped server-side** to no earlier than `now − 1h` (and if omitted, defaults to
  `now − 1h`) so an idle client can't request a flood.
- Read-only: it **never** writes `NotificationLog`. Dedup is the client's job.
- Response: `{ items: [{ reminderId, occurrenceStartUtc, title, location }] }`
  (`occurrenceStartUtc` as a UTC `DateTimeOffset` at the API boundary per repo convention;
  `location` nullable).

Placement: a new `RemindersController` (there is none today) in
`calendarITCore/Controllers/`, plus a thin application/infrastructure query service so the window +
expansion logic is unit-testable without HTTP. The occurrence-window helper currently private to
`ReminderDispatchJob` (`OccurrencesInWindow`) should be extracted into a shared, testable location
both the job and the new query use, rather than duplicated.

### 2. Frontend — notification mode detection (`web/src/push/webPush.ts`)

Extend the enable flow:

- Try push subscribe (existing `ensurePushSubscribed`).
- On success → **push mode**.
- If push is unsupported/unavailable/subscribe throws, but `Notification.requestPermission()`
  returns `granted` → **local mode**.
- If permission denied → disabled (as today).

Persist the resolved mode per-browser in `localStorage` (e.g. key `calendarit.notifyMode` =
`'push' | 'local'`). Expose a `getNotifyMode()` helper. `disablePush()` also clears the stored mode
and stops the poller.

### 3. Frontend — the local poller (`web/src/push/localReminders.ts`, new)

- Runs only when: a user session is active **and** `getNotifyMode() === 'local'`.
- Polls `GET /api/reminders/due?sinceUtc=<last poll>` every ~30s, and immediately on window
  `focus` / `visibilitychange → visible`.
- For each returned item, shows a notification via the service-worker registration:
  `registration.showNotification(title, { body, tag: \`${reminderId}:${occurrenceStartUtc}\`, data: { url: '/' } })`.
  Body mirrors the push path: `"Starts at <local time>."` plus location when present.
- Started/stopped from the app shell (where the session lives), so it runs app-wide — not only on
  the Settings page.

### 4. Dedup (client)

- `tag = reminderId:occurrenceStartUtc` — the OS/browser collapses duplicates across multiple open
  tabs and across re-polls.
- A pruned `localStorage` set of already-shown `reminderId:occurrenceStartUtc` keys prevents
  re-showing after the user dismisses one and the poll repeats. Prune entries older than the poll
  retention window (e.g. keep the last ~2h) so the set can't grow unbounded.
- The dedup decision is a **pure function** (`(shownKeys, dueItems, now) => { toShow, nextShownKeys }`)
  so it can be unit-tested in isolation.

### 5. Settings UI (`web/src/SettingsPage.tsx`, `NotificationsCard`)

- Push mode: keep today's copy ("...even when CalendarIT isn't open").
- Local mode: show a hint — *"Notifications on this device work while CalendarIT is open in a tab or
  installed as an app."*
- Brave / FCM-off: the current scary "blocked" note becomes a graceful message explaining the
  in-app fallback is active, rather than implying notifications failed.

## Service worker

No changes. `public/sw.js` already implements `showNotification` (via the `push` handler) and
`notificationclick` (focus existing tab / open one). The page calls
`registration.showNotification` directly for the local path; clicks are handled by the existing
`notificationclick` listener.

## Data flow

1. User toggles "Enable notifications on this device" (Settings).
2. `webPush.ts` resolves push vs local mode, persists it.
3. If local mode, the app-shell poller starts.
4. Every ~30s / on focus: poller calls `/api/reminders/due?sinceUtc=<last>`.
5. Backend expands recurrence, filters to WebPush-channel reminders due in the window, returns items.
6. Client runs the pure dedup function; shows notifications for new items; updates the shown-keys set
   and `sinceUtc`.

## Precision

~30s poll + focus-trigger → ≤30s jitter. The server reminder job is already a 1-minute cadence, so
this is consistent with existing behavior.

## Testing

**Backend (xUnit):**
- `/due` returns only reminders whose trigger falls in the window; excludes past/future.
- Recurring events expand to the correct occurrence(s) in-window (mirror `ReminderDispatchTests`).
- Only `Channel == WebPush` reminders are returned (Email excluded).
- Results are scoped to the requesting user.
- `sinceUtc` clamping: a `sinceUtc` older than `now − 1h` is treated as `now − 1h`.

**Frontend:**
- The pure dedup function: new items shown once; already-shown keys suppressed; old keys pruned.
- Mode selection: push success → 'push'; push failure + granted permission → 'local'; denied →
  disabled.

**Manual cross-browser smoke:** Firefox (real push, no Google), Brave with FCM off (local
fallback), Chrome (real push).

## Out of scope

- Background delivery when the app is fully closed on a browser without push — inherently needs push
  or email; **Email already covers this**.
- External notification services (ntfy, Telegram, webhooks, SMS).
- A distinct new reminder channel / VALARM mapping change.
- Any database migration.
