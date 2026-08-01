# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CalendarIT is a self-hosted calendar: an ASP.NET Core (.NET 10 preview) Web API backend plus a
React + Vite + TypeScript SPA. It does events, recurring events, reminders (email; browser Web
Push is in progress), .ics import/export, a CalDAV server for phone sync, and iMIP email
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

Both at once: `./deploy/dev.ps1` (or `dev.sh`) runs backend + frontend in one terminal.
Demo data: `./deploy/seed.ps1` seeds user `test@test.com` / `Test1234#1234`.
Docker (production shape): `docker compose up --build` — single container, nginx serves the SPA
and reverse-proxies `/api` to the .NET API. Requires `JWT_SIGNING_KEY` (≥32 chars) in `.env`.

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

### Recurring events

Events store an **RRULE master** (`RRule` + newline-separated `ExDates`); occurrences are **expanded
on read**, server-side, DST-correctly, via Ical.Net in `Infrastructure/Calendars/RecurrenceExpander`.
This expansion runs everywhere occurrences matter (event listing, reminders, CalDAV, search).
Time-zone ids are normalized on save; unknown ids are treated as "floating" via the `TimeZones`
helper — the single place that decision is made.

### Auth

ASP.NET Core Identity (Guid keys, `ApplicationUser`) + JWT access tokens with **rotating refresh
tokens** (stored as SHA-256 hashes; reuse detection revokes the whole chain). CalDAV can't use JWT,
so `/dav` uses **HTTP Basic validated against the same Identity user store** (not separate app
passwords) — relies on operator TLS, with a credential cache to avoid PBKDF2 per request. Login
lockout (10 fails / 15 min) is enforced manually in both `AuthService` and the CalDAV handler.

### Background jobs (Quartz.NET, every minute)

- `ReminderDispatchJob` — recurrence-expanded, timezone-correct, idempotent via `NotificationLog`.
  Its `WebPush` branch is currently a stub (Phase 5b). Reminders send **email through the owner's
  own connected mail account**, not a global relay — there are no `SMTP_*` env vars.
- `InvitationInboxJob` — scans each user's IMAP inbox read-only for iMIP REQUEST/CANCEL/REPLY.

### Security posture: outside input is hostile

Anything arriving by email, .ics import, or CalDAV PUT is untrusted. Inbound iMIP messages are
verified against the sender (`ImipMime.IsFromClaimedSender`, From-only) so nobody can inject events
as someone else. UIDs are caller-supplied, so internal invite copies are matched by a server-stamped
`SourceOrganizerUserId`, never UID alone. Password-reset links come only from `PUBLIC_BASE_URL`
(never the request Host) to avoid an enumeration/token-exfil oracle.

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
`TimePicker`). Cross-cutting prefs (clock 12/24h, week-start) are React contexts that seed from
localStorage for first paint and reconcile to the server profile.

**Canonical dev port is 5299** (backend http profile). The Vite proxy and `gen:api` both target it.

## Conventions

- Never commit and push by yourself, onyl when asked by the user
- If asked to commit and or push, commit under local git user of the system.
- `web/.npmrc` sets `legacy-peer-deps` (openapi-typescript's peer wants TS 5; the repo runs TS 6).

## Rules

- Be brief. Be concise in your answers. Avoid being too verbose or give unnecessary information
