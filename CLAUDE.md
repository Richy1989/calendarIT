# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CalendarIT is a self-hosted calendar: an ASP.NET Core (.NET 10 preview) Web API backend plus a
React + Vite + TypeScript SPA. It does events, recurring events, reminders (email and browser
Web Push), .ics import/export, a CalDAV server for phone sync, and iMIP email
invitations. `README.md` "Status" is the authoritative feature list. `ARCHITECTURE.md` is design
rationale only and its header/phase list can lag reality — trust the code over both.

## Commands

Backend (run from `core/calendarITCore/`):

```bash
dotnet build                              # build the solution (calendarITCore.slnx)
dotnet test                               # run all xUnit tests
dotnet test --filter "FullyQualifiedName~ReminderDispatch"   # one test class/method
cd calendarITCore && dotnet run           # run the API on http://localhost:5299 (Development)
```

Frontend (run from `web/`):

```bash
npm install
npm run dev        # Vite dev server on http://localhost:5173, proxies /api to :5299
npm run build      # tsc -b && vite build
npm run lint       # oxlint
npm run gen:api    # regenerate src/api/schema.d.ts from the LIVE backend OpenAPI (backend must be running on :5299)
```

Android app (run from `android/`; JDK 21, Android SDK with platform 37.2):

```bash
./gradlew assembleFossDebug          # app/build/outputs/apk/foss/debug/ (id org.hyperstarit.planit.debug)
./gradlew testFossDebugUnitTest      # JVM unit tests
./gradlew lintFossDebug
```

Release signing: keystore + `keystore.properties` in the repo-root `secrets/` folder (everything
there but its README and `keystore.properties.example` is git- and docker-ignored — never commit
or `git add -f` anything from it), or `PLANIT_KEYSTORE_*`/`PLANIT_KEY_*` env vars; without them
release builds are unsigned. See `secrets/README.md`.

Flavors: `foss` (no Google libraries: GitHub/F-Droid) and `play`. Debug builds allow plain http
(the emulator reaches a dev backend at `http://10.0.2.2:5299`); release builds are https-only.

Both at once: `./deploy/dev.ps1` (or `dev.sh`) runs backend + frontend in one terminal.
Demo data: `./deploy/seed.ps1` seeds user `test@test.com` / `Test1234#1234`.
Docker: one `Dockerfile`, two targets. The default (`runtime`, what `docker compose up --build`
builds) is the .NET app serving API + SPA on :8080 next to Postgres; `--target bundle` (the
published image, Unraid) puts nginx on :80 in front of it with all state under `/data`. Both run
the app as an unprivileged user (`PUID`/`PGID`, default 1654) after chowning the data dir.
Compose requires `JWT_SIGNING_KEY` (≥32 chars) in `.env`.

### The build file-lock gotcha (hit this constantly)

The running dev app (`calendarITCore.exe` in `bin/Debug`) **locks the host DLLs**, so `dotnet build`
/ `dotnet ef` fail with MSB3021/MSB3027 file-lock errors while it's running. Worse: `dotnet ef
migrations add --no-build` will silently emit an **empty migration** from stale locked DLLs.
Fix: either stop the app first, **or** build/test against a separate output dir with `-c Release`
(`dotnet build -c Release`, `dotnet test -c Release`) so you never touch the locked `bin/Debug`.

## Architecture

### Backend: layered solution with a dual-database twist

Clean layering, dependencies point inward: `CalendarIT.Domain` (entities, no deps) ←
`CalendarIT.Application` (interfaces + DTOs) ← `CalendarIT.Infrastructure` (EF, services, jobs,
mail, iCal). The host `calendarITCore` (controllers, DI, Program.cs) references Infrastructure;
`CalendarIT.CalDav` is a separate project for the hand-rolled CalDAV server.

**Two database providers share one model.** `DATABASE_PROVIDER` (env) selects Postgres (primary)
or SQLite (fallback, default). Migrations live in **separate assemblies per provider** —
`CalendarIT.Migrations.Postgres` and `CalendarIT.Migrations.Sqlite` — because their SQL differs.
`AppDbContextFactory` is the design-time factory both `dotnet ef` and the app use.

**Adding a migration means adding it to BOTH assemblies**, e.g.:

```bash
# from core/calendarITCore/
dotnet ef migrations add <Name> --project CalendarIT.Migrations.Postgres --startup-project calendarITCore
dotnet ef migrations add <Name> --project CalendarIT.Migrations.Sqlite   --startup-project calendarITCore
# (set DATABASE_PROVIDER for the target provider; let ef BUILD — do not pass --no-build, see gotcha above)
```

Migrations auto-apply on startup (`APPLY_MIGRATIONS`, default true).

**Timestamps are UTC `DateTime`, never `DateTimeOffset`.** SQLite can't `ORDER BY` / compare
`DateTimeOffset` in SQL, so events/calendars store UTC `DateTime`. `DateTimeOffset` appears only at
the API boundary (DTOs) — convert with `.UtcDateTime` inbound and
`new DateTimeOffset(DateTime.SpecifyKind(..., DateTimeKind.Utc))` outbound. (`RefreshToken` is the
one exception — it keeps `DateTimeOffset` because it's never SQL-ordered.)

**All-day events are dates, not instants**: stored as the date's midnight UTC (end = the inclusive
last day) and the same date wherever they're viewed. Range queries therefore match them against the
window's *wall clock* — the SPA sends `from`/`to` with its offset (`toOffsetIso`), never
`toISOString()` — and reminders count from midnight in the event's own zone
(`ReminderOccurrences`), with text that names the date. Never run an all-day start through a zone
conversion or `new Date(iso)` for display (`allDayDate` in `web/src/lib/dates.ts`).

**An event's zone is its own**: set from the browser on create, returned as `EventDto.TimeZone`
and sent back unchanged on edit (null on update = keep). Changing the zone of a timed series moves
it, like a rule change.

### Recurring events

Events store an **RRULE master** (`RRule` + newline-separated `ExDates`); occurrences are **expanded
on read**, server-side, DST-correctly, via Ical.Net in `Infrastructure/Calendars/RecurrenceExpander`.
This expansion runs everywhere occurrences matter (event listing, reminders, CalDAV, search).
Time-zone ids go through the `TimeZones` helper — the single place that decides what an id means:
`TimeZones.Canonical` gives NodaTime's tz-database spelling (any case, Windows names, vendor
prefixes like `/mozilla.org/…/Europe/Berlin`), and that spelling is what's stored and what's
handed to Ical.Net, which silently reads an id it doesn't know as UTC. An incoming TZID that is
no zone name but has a VTIMEZONE (Outlook's "(UTC+01:00) Amsterdam, Berlin…") is matched to the
real zone with the same clocks (`ZoneDefinition`); unresolvable ids are "floating".

- **Rules are validated on the way in** (`RecurrenceRules`: unparseable or sub-hourly → 400 from the
  API, dropped to a one-off from CalDAV/import/iMIP) and **expansion never throws or runs
  unbounded** (`MaxUnmatchedIncrementsLimit`, per-call and per-response occurrence caps). One bad
  row must not break a user's calendar or the reminder job that serves everyone.
- **Edited occurrences are override rows**: a normal one-off event with `SeriesMasterId` +
  `RecurrenceIdUtc` (iCalendar RECURRENCE-ID), same `Uid`/calendar as the master. The master's
  `ExDates` holds deletions **and** overridden instants, so every expansion skips them for free;
  `ICalEventMapper.ToICalEvents` subtracts the override instants again when writing EXDATE.
  Anything looking events up by UID must filter `SeriesMasterId == null`.
- **"This and following" splits the series** (`EventService.SplitSeriesAsync`, `PUT
  /api/events/{id}/following`): the old master gets an UNTIL just before the occurrence, a new
  master with a **new UID** starts there (COUNT shared out); exclusions/overrides from there on
  move to it unless its rule or slot changed. Guests get the shortened series as an update and
  the new one as an invitation (statuses reset). On the first occurrence it is a whole-series
  edit. `DELETE …/following` only shortens. Neither is on the undo stack.
- `SeriesWriter` writes a whole iCalendar resource (master + overrides) for CalDAV PUT, import and
  iMIP. A CalDAV resource = one master with its overrides; overrides never appear as their own
  resource, and any change to an override bumps the master's `UpdatedAt` (the ETag).
- **CalDAV ETags use microseconds** (`Ticks / 10`) — Postgres truncates timestamps to µs.
- **CalDAV incremental sync** (RFC 6578 `sync-collection`): the sync token is the time it was
  issued; changes are masters with `UpdatedAt` from 2 min before it (overlap for late commits),
  deletions come from `DeletedResources`, which `AppDbContext.SaveChanges` writes for every
  master deleted or moved to another calendar — so every write path must keep bumping
  `UpdatedAt` on a change. Tokens live 60 days (records are pruned after 61).
- The web editor builds custom rules in `web/src/lib/rrule.ts` (FREQ/INTERVAL/BYDAY/COUNT/UNTIL).
  It never rewrites a rule it can't fully parse, and only rebuilds the text when the user changes
  it — a changed rule counts as a moved series server-side, which drops exceptions and overrides.

**Calendar default category**: `Calendar.DefaultCategoryId`. An event without its own category
*shows* in it (color, `EffectiveCategoryId`, category counts, ICS/CalDAV `CATEGORIES`) without
being written to it, so recoloring the calendar recolors its events. Incoming iCalendar data
naming the default leaves the event inheriting, and a bare `COLOR` isn't snapped to a category in
such a calendar.

**Calendar subscriptions** (`CalendarSubscriptionService`, `SubscriptionRefreshJob` every 5 min):
a calendar with `SourceUrl` set mirrors an .ics feed by UID and is **read-only** everywhere —
`EventService` (`EnsureWritableAsync`), import, CalDAV PUT/DELETE (403, privilege set "read").
`DefaultCalendar` never picks one, and an account always keeps one calendar of its own. Feeds are
fetched by `CalendarFeedFetcher`: public addresses only (`OutboundHostPolicy` on each connection),
redirects by hand (≤5), a login in the URL never sent to another host, 5 MB cap, conditional GET.
An unchanged event keeps its `UpdatedAt` across refreshes (CalDAV ETags stay put).

**Tasks** (`TaskItem`, table `Tasks`, `TaskService`, `/api/tasks`): to-dos in the user's own
calendars, synced over CalDAV as **VTODO resources in the same collection** as the events (one
resource name space per calendar: a UID is either an event or a task, PUT of the other kind →
409). `TaskICalMapper`/`TaskWriter` map VTODOs; subtasks are one level deep — `RelatedToUid`
keeps the RELATED-TO as sent (a child may arrive before its parent and is linked later), deeper
trees are flattened onto the top task. Tasks don't repeat; a phone's RRULE is only kept.
Task reminders (`TaskReminder`, `TaskReminders`) run inside `ReminderDispatchJob` and the
polling query: they count back from the due time — 09:00 in the calendar's zone for a date-only
due — and are logged in `NotificationLog` under the stored due, so a moved due reminds again.
They aren't synced as VALARMs (a phone would alarm twice).

**App sync API** (`/api/sync`, `/api/devices`; `Infrastructure/Sync`) — for the native Android app
(PlanIt, `org.hyperstarit.planit`, offline-first). Pull = paged delta by an opaque cursor (same
`UpdatedAt` + 2-min overlap + `DeletedResources` facts as CalDAV sync; tombstones carry
`Kind`/`EntityId`), events as stored series (master + overrides, never expanded), calendars and
categories always whole. Push = ordered ops that each call the same service method as the REST
endpoint (`SyncOps`), creates take client-chosen ids (`Save*Request.Id`, `ClientIds`), event/task
changes need `baseVersion` (µs `UpdatedAt`; the series' for any occurrence) → `conflict` when
stale, the server copy wins. Within one push, later ops on the same series/task are judged by the
base its first accepted op had (none after a create there), and each `ok` result returns
`versions` (the touched things' new versions) for ops still queued on the device. Devices hold content-free push targets (UnifiedPush/FCM), kept apart
from browser `PushSubscriptions` (the app alarms locally). Any new write path must keep bumping
`UpdatedAt`, or the app never sees the change.

### Auth

ASP.NET Core Identity (Guid keys, `ApplicationUser`) + JWT access tokens with **rotating refresh
tokens** (stored as SHA-256 hashes; reuse detection revokes the whole chain). CalDAV can't use JWT,
so `/dav` uses **HTTP Basic validated against the same Identity user store** (not separate app
passwords) — relies on operator TLS, with a credential cache to avoid PBKDF2 per request. Login
lockout (10 fails / 15 min) is enforced manually in both `AuthService` and the CalDAV handler.

**Sessions**: each sign-in is a session (`RefreshToken.SessionId`, constant across rotations);
access tokens carry it as `sid`, and `SessionValidator` (JwtBearer `OnTokenValidated`, cached 30s,
evicted on revoke) rejects tokens whose session was signed out — revocation is immediate.
Rotation claims the old token with a conditional UPDATE (one winner per race), and a token
re-presented within 30s of its rotation is a sibling tab, not theft (no chain revocation). The SPA
also serializes refreshes across tabs with the Web Locks API. Unknown/locked accounts still burn a
password hash (`PasswordTiming`) so timing can't enumerate users; CalDAV failures are throttled
per IP (`CalDavFailureThrottle`); a password change bumps `CredentialEpochs`, retiring cached
CalDAV logins.

### Background jobs (Quartz.NET)

- `ReminderDispatchJob` — recurrence-expanded, timezone-correct, idempotent via `NotificationLog`.
  Email reminders go **through the owner's own connected mail account**, not a global relay —
  there are no `SMTP_*` env vars. Per-reminder failures are isolated (logged, never abort the
  run). WebPush reminders are VAPID-signed (`WebPushSender`); keys come from `VAPID_*` env or
  are auto-generated and persisted (`VapidKeyStore`, `vapid.json`). Browsers
  without a push service fall back to polling `GET /api/reminders/due` (`web/src/push/localReminders.ts`).
  Browser reminders carry a **snooze token** (`ReminderSnoozes`: data-protection sealed, 2 days,
  one reminder occurrence): the service worker's Snooze button posts it to the anonymous
  `POST /api/reminders/snooze`, and the job pushes `ReminderSnoozes` rows again when due. A
  zone-less all-day event (from a phone) reminds at midnight in its calendar's zone, else that
  of the calendar's latest zoned event (`ReminderOccurrences.FallbackZonesAsync`), else UTC.
- `OutboxDispatchJob` (every 30s + poked on enqueue) — **all mail is queued** (`IMailOutbox` →
  `OutboxMessages`) and sent per user over one SMTP session, with backoff, expiry per kind, and
  user-facing error text (`MailConnections.Describe`). Never send SMTP inline in a request.
  Reminders stage their outbox row in the same `SaveChanges` as the `NotificationLog` row.
- `MaintenanceJob` (nightly) — prunes expired refresh tokens, old dispatch logs, old outbox rows.
- `InvitationInboxJob` — scans each user's IMAP inbox read-only for iMIP REQUEST/CANCEL/REPLY.

### Security posture: outside input is hostile

Anything arriving by email, .ics import, or CalDAV PUT is untrusted — `ICalEventMapper.Apply`
clips text to the column widths (Postgres enforces them; SQLite doesn't, so tests won't notice).
Inbound iMIP messages are verified against the sender (`ImipMime.IsFromClaimedSender`, From-only):
an unverified REPLY/CANCEL is ignored, so nobody can answer for a guest or delete your events; an
unverified REQUEST (a forwarded invite) is delivered, but only ever as pending (NeedsAction).
UIDs are caller-supplied, so internal invite copies are matched by a server-stamped
`SourceOrganizerUserId`, never UID alone. Password-reset links come
only from `PUBLIC_BASE_URL` (never the request Host) to avoid an enumeration/token-exfil oracle.
User-named hosts (mail servers, push endpoints) are SSRF-guarded on the address actually dialled (`OutboundHostPolicy`;
`MAIL_HOST_POLICY`, push is always public-only). Security headers (CSP etc.) are set by the app
itself (`SecurityHeaders`), so they hold without nginx; nginx hides the proxied duplicates.
Errors leave as ProblemDetails: throw `InvalidInputException` for caller-fixable input → 400.

**Logging** (`calendarITCore/Logging/`): `ConsoleLogFormatter` writes every line — short,
aligned, coloured with ANSI escapes it writes itself (never a Serilog console theme: themes only
apply on a terminal, and a container's stdout never is one); `NO_COLOR` turns colour off.
`UseRequestLog` logs the path as requested and **never the query string or referer** — URLs
can carry tokens (a reset link does); don't add `QueryString`, `$request` or `$http_referer` to
any log, ours or nginx's. Everything the formatter writes, message text and values alike, has its
control characters shown as `\xNN`, so outside input can't drive the terminal showing the log
(structured properties are still better: they get highlighted). Levels live in the `Serilog`
section of `appsettings.json`; the format lives in code. nginx (bundle image) logs only what
never reached the API, in the same layout (`deploy/nginx.conf`); the release workflow checks
the built image's log for leaked tokens and raw escapes.

### Android app (PlanIt)

Native Kotlin in `android/` (`org.hyperstarit.planit`, minSdk 34): Compose + Material 3, Hilt,
Room (the offline source of truth; schemas exported to `app/schemas`, never a destructive
migration), DataStore, OkHttp + kotlinx.serialization (`net/ApiClient`, hand-written models of the
sync protocol), WorkManager (`SyncWorker`: every 15 min + after each change/app start).

- **Accounts** (`accounts/Accounts`): several at once, kind `planit` (now) or `caldav` (later);
  every row carries `accountId`. First launch picks the kind. Per-account tokens sealed with an
  Android Keystore AES-GCM key (`SecretStore`/`KeystoreSecretBox`, never backed up); `AccountAuth`
  serializes refreshes (rotating tokens) and a refused refresh marks the account `needsSignIn` —
  nothing is sent for it until the user signs in again (server lockout). Removing an account
  deletes all of its rows.
- **Self-signed certificates**: trust on first use, per account. `Certificates.probe` reads the
  certificate (handshake only, then refused — nothing is ever sent), the user compares the
  SHA-256 fingerprint, `certificatePin` stores it; `Certificates.clientFor` accepts system-trusted
  certificates or exactly the pinned one (any host name). Another one → `certificateChanged`,
  sync stops until reviewed. OkHttp tries IPv4/IPv6 side by side and hides TLS failures as
  *suppressed* exceptions: test with `isCertificateRefusal()`/`allFailures()`, not the cause chain.
- **Offline-first store** (`data/db`): server copies (`events`, `overrides`, `tasks`) are written
  only by sync; the user's pending changes live in same-shaped `*_edits` tables and the queries
  read both (an edit wins). `sync/LocalChanges` writes the edit and queues an `outbox` op (folding:
  edit into a pending create/edit, create+delete cancels). `sync/PlanItSync`: push (results:
  ok → drop op and rebase queued ops on returned `versions`; conflict/notFound/invalid/forbidden →
  drop that thing's queue + a `notices` row, "apply mine anyway" re-queues on the new version;
  error → keep with backoff), then pull (paged; calendars/categories whole; a reset or first
  listing deletes what it didn't name), then drop edits nothing is waiting on. Room schemas are
  exported (`app/schemas`), `MIGRATION_1_2` is validated in `MigrationTest` — never a destructive
  migration.
- **Calendar views** (`ui/calendar`): month (kizitonwose `HorizontalCalendar`), week/day (own
  hour grid, `calendar/TimelineLayout` puts overlaps side by side), agenda; a read-only detail
  sheet; calendar visibility and the last view are device prefs (`CalendarPrefs`, DataStore).
  `calendar/Occurrences` builds what a range shows from stored series + overrides (server rules:
  timed by the viewer's day instants, all-day by date — never through a zone), `calendar/Recurrence`
  expands with ical4j like `RecurrenceExpander` (own zone's clock, HOURLY in elapsed time, a start
  the rule doesn't produce isn't shown, never throws, bounded). Use ical4j's `getDates(…, max)` —
  iterating its lazy `getDatesAsStream` yielded nothing. **`testdata/recurrence-vectors.json`** is
  shared: `RecurrenceVectorsTests` (server) and `RecurrenceVectorsTest` (app) must both match it;
  add a case there for any expansion change (`UPDATE_RECURRENCE_VECTORS=1 dotnet test --filter
  RecurrenceVectors` regenerates from the server — review the diff). R8 keeps ical4j whole
  (`proguard-rules.pro`).
- **Editing** (`ui/editor`, `ui/tasks`): full-screen editors (`EventEditor` for new/one-off/
  "only this"/"this and following"/"all", `TasksScreen` with one level of subtasks); a repeating
  appointment asks for the scope first (`Scope`). `calendar/RepeatRule` is the editor's rule
  model — it never rewrites a rule it can't parse (`keptRule`). `sync/SeriesEdits` predicts what the
  server's `EventService` does to a series (occurrence edit/delete, split with UNTIL/COUNT
  sharing, delete following) so the change shows at once; `LocalChanges` queues the matching op
  (`event.occurrence.*`, `event.following.*`, `event.rsvp`). Deletes are held behind an Undo
  snackbar (`CalendarViewModel.pendingDelete`) and only then queued. The editor's view model
  outlives the dialog: each opening passes a fresh session token.
- **Reminders on the phone** (`reminders/`): `ReminderPlanner` (pure, server rules: only channel
  `WebPush` — email stays the server's; timed = start − minutes; all-day from midnight in the
  event's zone, else its calendar's, else the phone's; tasks before the due or 09:00 of a due
  date; keys name the occurrence so a reschedule keeps them) → `ReminderScheduler` sets exact
  alarms (`USE_EXACT_ALARM`) for the next 8 days, max 200, after every sync, local change, app
  start, boot, app update and clock/zone change (`RescheduleReceiver`, checks the action); a
  reminder missed by ≤15 min rings at once; rung keys are remembered (`ReminderPrefs`, plain
  SharedPreferences) so none rings twice. `ReminderReceiver` posts the notification (channel
  `reminders`) with Snooze 10 min and, for tasks, Done; everything it shows rides in the alarm's
  intent (no DB read when it fires). The phone is never a server push subscription, so web and
  phone reminders don't double. Drawer switch "Reminders on this phone"; notification permission
  asked once.
- **System calendar** (`system/`, opt-in drawer switch, needs READ/WRITE_CALENDAR): each PlanIt
  account is an Android account of type `R.string.account_type` (= the app id, so debug and
  release don't clash; `AuthenticatorService`), each calendar a provider calendar written as its
  sync adapter (`CALLER_IS_SYNCADAPTER`; `WRITE_SYNC_SETTINGS` to mark it syncable).
  `SystemCalendar.sync()` = import then export: other apps' DIRTY/DELETED/new rows become
  `LocalChanges` (create, whole edit, delete, new EXDATEs → occurrence deletes, exception rows →
  occurrence edit or, when cancelled, delete), then PlanIt's state is written over the provider
  (`ProviderMapping`: series with RRULE + DURATION + EXDATE of deletions only, edited occurrences
  as exception rows via ORIGINAL_SYNC_ID, all-day midnight UTC with an exclusive end; content
  fingerprint in SYNC_DATA2 skips unchanged rows). No reminder rows and MAX_REMINDERS=0: PlanIt
  rings them. Runs after every sync and local change, and when Android runs `CalendarSyncService`
  (another app changed something — Android schedules it, usually within a minute or two). Turning
  it off removes the Android accounts and with them their calendars. Tasks aren't there.
- Tests: Robolectric + MockWebServer (`SyncEngineTest`, `CertificatesTest` with a real
  self-signed TLS server, `MigrationTest`); `LiveServerTest` runs against a real backend only with
  `PLANIT_LIVE_URL`/`_EMAIL`/`_PASSWORD` set (throwaway account).

### Frontend

React 19 / Vite 8 / TS 6, FullCalendar for the grid, TanStack Query for server state. The API client
is **typed from the backend's live OpenAPI**: `npm run gen:api` regenerates `src/api/schema.d.ts`,
consumed via openapi-fetch (`src/api/client.ts`). Occasionally `schema.d.ts` is hand-edited for
ASP.NET int32 quirks (a field typed `number | string` needing `Number()` coercion) — check git
history before regenerating if something looks hand-tweaked. Auth token refresh is proactive in
`src/auth/session.ts` (`ensureAccessToken` refreshes ~30s before expiry and dedupes concurrent
refreshes; refresh tokens rotate, so the new one must be saved). Shared primitives live in
`src/lib/` (`dates.ts` date helpers, `useUndoStack.ts`) and `src/components/` (`Popover.tsx` portals
to `document.body` to escape the modal's `overflow-hidden`; `DateTimeField`, `MonthCalendar`,
`TimePicker`). On a desktop the calendar has a **collapsible left sidebar** (`components/Sidebar.tsx`: brand,
New, a month to jump in, calendar/subscription/category visibility) and **one top bar** that
`CalendarView` draws itself (FullCalendar's `headerToolbar` is off; navigation goes through the
calendar API): Today ‹ › title … view dropdown, search icon (`searchSlot`, opens on click or
typing), tasks, then the app's clock/profile (`toolbarEnd`). Phones (`NARROW_QUERY`) keep the app
header and FullCalendar's top/bottom bars with the calendar/category pickers. Creating an event in the grid opens `QuickCreate.tsx` (a popover beside the slot, a bottom sheet
on phones) on a draft `CalendarView` owns and draws live as a `.fc-draft-ghost` event; "More
options", editing, and the list view use `EventModal`. Both share `lib/eventForm.ts` (repeat
presets, reminder wording) and take a new timed event's reminders from its calendar
(`Calendar.DefaultReminders`, set in Settings → Calendars; all-day events start with none).
**Themes**: `index.css` defines Midnight (`:root`, default), Daylight and Graphite
(`:root[data-theme=…]`); every color in the CSS comes from those tokens (`color-mix()` for
alphas, never a hard-coded rgba/hex). The user's accent/highlight override `--accent` /
`--highlight` on `<html>`, and the gradient, links and ring derive from them, so set those two,
not their derivatives. `lib/appearance.ts` applies it before the first render (`main.tsx`);
`theme.tsx` syncs it with the profile (`Theme`/`AccentColor`/`HighlightColor`).
Cross-cutting prefs (clock 12/24h, week-start) are React contexts that seed from
localStorage for first paint and reconcile to the server profile.

**Canonical dev port is 5299** (backend http profile). The Vite proxy and `gen:api` both target it.

## Conventions

- Never commit and push by yourself, onyl when asked by the user
- If asked to commit and or push, commit under local git user of the system.
- **No Claude attribution in commits**: no `Co-Authored-By: Claude …`, no "Generated with Claude
  Code", no session links — nothing mentioning Claude in commit messages (or PR descriptions).
- **Close GitHub issues from the commit**: when a commit implements or fixes an issue, end the
  message body with a closing keyword per issue, e.g. `Closes #1` / `Fixes #2` (one keyword per
  issue: `Closes #1, closes #2`). GitHub closes them when the commit (or the PR containing it)
  lands on `main`. Check open issues first (`gh issue list`, or the public API if `gh` isn't
  logged in) so related work isn't missed.
- **Existing databases must keep working on update — no data loss, ever.** Schema or data changes
  go through migrations in *both* provider assemblies; existing rows are migrated, backfilled or
  renamed, never dropped (e.g. duplicate UIDs were renamed, not deleted). Verify the upgrade on a
  database created by the previous version (SQLite and Postgres), not just a fresh one.
- **Releasing**: write the notes first as `docs/releases/vX.Y.Z.md` (layout as in the existing
  files: `# CalendarIT X.Y.Z`, logo, "_Changes since `vPREV`._", ✨ New Features / 🔧
  Improvements / 🔒 Security / 🐛 Fixes / 📦 Deployment & Docs, Buy-Me-A-Coffee link; user-facing
  wording, issue numbers in brackets), commit them to `main`, then push a plain tag
  `git tag vX.Y.Z && git push origin vX.Y.Z`. `release.yml` first runs the full test suite
  (`test.yml`, which also runs on every push to main and every PR), then builds the bundle image, checks its
  log, pushes (with SBOM + provenance attestations) `richy1989/calendarit:X.Y.Z` (+ `:latest` unless the tag has a `-suffix`), and creates
  the GitHub Release from the notes file + Docker line + GitHub's changelog link. Versions are
  `0.x` (the `v8.0.0` tag was a typo for 0.8.0; the next after 0.9.0 is 0.10.0, never 8.x).
- **Keep the About page in sync**: when a library listed there (`FRONTEND_LIBRARIES` /
  `BACKEND_LIBRARIES` in `web/src/SettingsPage.tsx`) is added, removed, replaced, or changes its
  name/license/URL, update that list in the same change.
- `web/.npmrc` sets `legacy-peer-deps` (openapi-typescript's peer wants TS 5; the repo runs TS 6).

## Rules

- Be brief. Be concise in your answers. Avoid being too verbose or give unnecessary information
