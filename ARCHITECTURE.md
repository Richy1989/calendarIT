# CalendarIT — Architecture

> Status: **Planning** (no code yet)
> Last updated: 2026-07-21

A modern, browser-based calendar application with a .NET backend, user accounts,
standard iCalendar file support, and CalDAV sync so mobile clients (via **DAVx⁵**)
can subscribe to a user's calendars.

---

## 1. Product Summary

A self-hostable calendar app that runs in the browser. Users sign in, manage one or
more calendars, create one-off and recurring events, get reminders (email + browser
push), and sync their calendars to their phone through DAVx⁵ over CalDAV. The whole
system ships as Docker containers and runs behind an operator-supplied reverse proxy.

### v1 Scope

- User accounts (register / login / logout).
- Personal calendars (one or more per user).
- Events: create, edit, delete; all-day and timed.
- **Recurring events** (RRULE) with exceptions (EXDATE / modified occurrences).
- **Timezone support** — correct storage and display across DST.
- **Reminders / notifications** via **email** and **Web Push**.
- **iCalendar (.ics) import & export.**
- **CalDAV server** endpoint for DAVx⁵ / native calendar clients.
- Runs in **Docker**; Postgres as primary DB, SQLite as fallback; config via env vars.

### Explicitly out of scope for v1

- Live two-way sync with Google / Outlook / Microsoft 365 (no external OAuth providers).
- Real-time collaborative editing.
- Native mobile apps (the phone story is DAVx⁵ + the device's built-in calendar).

---

## 2. High-Level Decisions (locked)

| Area | Decision |
|---|---|
| Frontend | **React + TypeScript + Vite** (SPA) |
| Calendar UI | **FullCalendar** (React) for month/week/day views |
| API consumption | **openapi-typescript** (types from OpenAPI) + **TanStack Query** hooks |
| Backend | **ASP.NET Core Web API (.NET)** |
| API style | **REST controllers + OpenAPI/Swagger** |
| Auth | **ASP.NET Core Identity + JWT** |
| Primary DB | **PostgreSQL** (via EF Core) |
| Fallback DB | **SQLite** (via EF Core, same model/migrations) |
| ORM | **EF Core** |
| Background jobs | **Quartz.NET** |
| iCalendar library | **Ical.Net** (parse/serialize RRULE, VEVENT, VTIMEZONE) — candidate |
| CalDAV | **Adapt an existing .NET WebDAV/CalDAV library** over our shared event core |
| Recurrence storage | **Store RRULE, expand on read** for a queried date range |
| Notifications | **Email (SMTP)** + **Web Push (VAPID)** |
| Packaging | **Docker** (backend, frontend, Postgres); operator brings their own reverse proxy |
| TLS | Terminated by the **operator's reverse proxy**; app serves HTTP + honors `X-Forwarded-*` |
| Logging | **Structured logging (Serilog)** — JSON to stdout in prod, **colorful themed console** in dev, correlation IDs, configurable levels |

---

## 3. System Overview

```
                         ┌───────────────────────────────────────────┐
                         │        Operator's Reverse Proxy (TLS)       │
                         │      (Caddy / Traefik / nginx — external)   │
                         └───────────────┬─────────────────┬───────────┘
                                         │ https           │ https
                    browser (React SPA)  │                 │  DAVx⁵ / CalDAV clients
                                         │                 │
                              ┌──────────▼─────────────────▼──────────┐
                              │        ASP.NET Core Backend            │
                              │                                        │
                              │  ┌────────────┐  ┌──────────────────┐  │
                              │  │ REST API   │  │ CalDAV endpoints │  │
                              │  │ (OpenAPI)  │  │ (WebDAV/CalDAV)  │  │
                              │  └─────┬──────┘  └────────┬─────────┘  │
                              │        │                  │            │
                              │  ┌─────▼──────────────────▼─────────┐  │
                              │  │      Domain / Application Core     │  │
                              │  │  events, calendars, recurrence,    │  │
                              │  │  iCal (Ical.Net), timezones        │  │
                              │  └─────┬───────────────────┬─────────┘  │
                              │        │                   │            │
                              │  ┌─────▼──────┐    ┌────────▼─────────┐  │
                              │  │ EF Core    │    │  Quartz.NET      │  │
                              │  │ (Pg/SQLite)│    │  (jobs/reminders)│  │
                              │  └─────┬──────┘    └────────┬─────────┘  │
                              └────────┼────────────────────┼───────────┘
                                       │                    │
                              ┌────────▼─────┐      ┌────────▼─────────┐
                              │  PostgreSQL  │      │ SMTP / Web Push  │
                              │  (or SQLite) │      │  (outbound)      │
                              └──────────────┘      └──────────────────┘
```

**Key principle:** the REST API, iCal import/export, and the CalDAV server all sit on
**one shared domain core and one event store**. CalDAV is another protocol *over* the
same data, never a parallel copy.

---

## 4. Backend Architecture

### 4.1 Project layout (proposed)

**Current state (scaffolded):** a .NET 10 ASP.NET Core Web API from the default
template, with OpenAPI already wired. This is the `calendarITCore` host project.

```
/core
  calendarITCore/
    calendarITCore.slnx               # solution
    calendarITCore/                   # the Web API host project (net10.0)
      Program.cs                      # minimal hosting; OpenAPI enabled
      Controllers/                    # WeatherForecast sample (to be removed)
      appsettings*.json
      calendarITCore.csproj
```

**Target structure** — grow the solution into these projects (added as siblings under
`/core/calendarITCore/` and referenced from the solution). Names align with the existing
`calendarITCore` host; the host keeps its current name/role:

```
/core/calendarITCore
  calendarITCore              # ASP.NET Core host (existing): controllers, DI, auth, OpenAPI, CalDAV wiring
  CalendarIT.Application      # use cases/services, DTOs, validation, recurrence expansion
  CalendarIT.Domain           # entities, value objects, domain rules (framework-free)
  CalendarIT.Infrastructure   # EF Core, DB providers, iCal (Ical.Net), email, push, Quartz jobs
  CalendarIT.CalDav           # WebDAV/CalDAV protocol layer adapting the shared core
  CalendarIT.Tests            # unit + integration tests
```

Layered/clean-ish separation; not over-engineered. `Domain` has no framework
dependencies so recurrence and timezone logic can be unit-tested in isolation.

**Template cleanup TODO:** remove the `WeatherForecast` controller/model; net10.0 uses
`Microsoft.AspNetCore.OpenApi` (no Swashbuckle by default) — confirm the OpenAPI doc
setup we generate the TS client from; and **drop `UseHttpsRedirection()`** since the app
serves plain HTTP behind the operator's reverse proxy (TLS is terminated upstream).
The frontend (React + Vite) will live in a sibling top-level folder (e.g. `/web`).

**Toolchain confirmed (2026-07-21):** .NET SDK **10.0.400-preview** (preview, not GA —
pin package versions, expect occasional preview edges), Node **v22**, Docker **29**.

**Skeleton built (2026-07-21):** the target multi-project solution now exists
(`calendarITCore` host + Domain/Application/Infrastructure/CalDav/Tests, wired with the
reference graph above). Template cleanup done: WeatherForecast removed, `UseHttpsRedirection`
dropped, `ForwardedHeaders` (X-Forwarded-For/Proto) enabled for the proxy. **Serilog**
logging is live — colourful ANSI console in dev, compact JSON in prod, `LOG_FORMAT` /
`LOG_LEVEL` (+ `LOG_LEVEL__<Namespace>`) config, per-request correlation via
`UseSerilogRequestLogging`. Health endpoints `**/health**` (liveness) and `**/ready**`
(readiness; picks up checks tagged `ready`) return 200. Solution builds warning-free.
**Security TODO — resolved:** the `Microsoft.OpenApi` NU1903 advisory is cleared by
pinning `Microsoft.OpenApi 2.11.0` (and `Microsoft.AspNetCore.OpenApi 10.0.10`).

### 4.2 Data model (first cut)

- **User** — provided by ASP.NET Core Identity (Id, email, password hash, etc.).
- **Calendar** — `Id`, `OwnerUserId`, `Name`, `Color`, `TimeZoneId`, `Ctag` (CalDAV
  collection change tag), timestamps.
- **Event** — `Id`, `CalendarId`, `Uid` (iCal UID, stable across clients), `Summary`,
  `Description`, `Location`, `StartUtc`, `EndUtc`, `StartTimeZoneId`, `IsAllDay`,
  `Color` (hex, nullable — see color sync note), `RRule` (nullable), `RDate`/`ExDate`
  sets, `RecurrenceId` (for modified occurrences), `SequenceNumber`, `ETag`, `Created`,
  `LastModified`, `Status`.
- **Reminder / Alarm** — `Id`, `EventId`, `TriggerOffset` (e.g. -15m), `Channel`
  (Email | WebPush), `Enabled`. Maps to iCal VALARM.
- **PushSubscription** — `Id`, `UserId`, endpoint, p256dh, auth (for Web Push VAPID).
- **NotificationLog** — dedupe/idempotency for dispatched reminders.

Recurrence: **store `RRULE`/`EXDATE`/`RDATE` as the source of truth**; expand
occurrences on read for the requested window using Ical.Net. Modified single
occurrences are stored as override rows keyed by `Uid` + `RecurrenceId`.

### 4.2b Event color & CalDAV sync

Per-event color is standardized: **RFC 7986** adds a `COLOR` property to `VEVENT`, but
its value must be a **CSS3 color *name*** (e.g. `COLOR:turquoise`), not arbitrary hex.
Client support varies (Apple also uses non-standard `X-APPLE-CALENDAR-COLOR`).

Design:
- Store the **exact hex** on `Event.Color` for our own web UI (full fidelity).
- When serializing to **iCalendar / CalDAV**, emit the `COLOR` property as the **nearest
  CSS3 color name** so DAVx⁵ / phone calendars get a color. On import, map an incoming
  CSS3 `COLOR` name back to its hex.
- The web app's **default swatches are exact CSS3-named colors** (mediumslateblue,
  turquoise, mediumseagreen, goldenrod, cornflowerblue, palevioletred, tomato, slategray)
  so they round-trip losslessly; a **custom hex picker** is allowed and snaps to the
  nearest name on the wire. (Actual `COLOR` (de)serialization lands with iCal/CalDAV in
  Phases 4/6; the web UI already stores/edits the color.)

### 4.3 Timezone strategy

- Store timestamps in **UTC** plus the **originating IANA time zone id** (e.g.
  `Europe/Berlin`) so recurrence + DST are computed correctly (a "09:00 daily" event
  must stay 09:00 local across DST changes).
- All-day events stored as floating dates (no tz).
- iCal export emits proper `VTIMEZONE` / `TZID`; import preserves them.
- Frontend displays in the user's browser tz (with per-calendar tz override possible).

### 4.4 API layer

- REST controllers, JSON, **OpenAPI/Swagger** document generated at build/runtime.
- Auth: **ASP.NET Core Identity** for the user store; **JWT** bearer tokens issued on
  login and sent by the SPA. **Access + rotating refresh token** strategy: short-lived
  access token, rotating refresh token, `/api/auth/refresh` endpoint; refresh tokens
  tracked server-side for revocation/rotation. (CalDAV keeps its own credential path —
  app passwords / Basic-over-TLS — separate from JWT; see §4.5.)
- Honors `X-Forwarded-For` / `X-Forwarded-Proto` (ForwardedHeaders middleware) since it
  always runs behind a proxy.
- Representative endpoints (illustrative):
  - `POST /api/auth/register`, `POST /api/auth/login`, `POST /api/auth/refresh`
  - `GET/POST /api/calendars`, `GET/PUT/DELETE /api/calendars/{id}`
  - `GET /api/calendars/{id}/events?from=&to=` (returns expanded occurrences)
  - `POST/PUT/DELETE /api/events/{id}` (with "this / this-and-future / all" edit modes)
  - `POST /api/calendars/{id}/import` (.ics upload), `GET /api/calendars/{id}/export.ics`
  - `POST /api/push/subscribe`, reminder CRUD nested under events.

### 4.5 CalDAV server

- DAVx⁵ speaks **CalDAV** (WebDAV + calendar extensions: `PROPFIND`, `REPORT`,
  `PUT`, `DELETE`, `MKCALENDAR`, sync-token / ctag, ETags).
- Approach: **adapt an existing .NET WebDAV/CalDAV library** as the protocol layer,
  backed by our shared event store and Ical.Net serialization.
- **Open research item (spike required):** pick the library. Candidates to evaluate —
  `NWebDav` (OSS WebDAV base, would need CalDAV extensions), IT Hit WebDAV Server for
  .NET (has CalDAV; commercial, free tier), or porting SabreDAV-style logic. Decision
  recorded here after a spike. This is the **highest-risk** component.
- CalDAV auth: DAVx⁵ needs an auth scheme it supports — **HTTP Basic over TLS** (proxy
  provides TLS) or app-specific passwords. JWT is not suitable for DAVx⁵; plan a
  separate credential path for the DAV endpoint (e.g. app passwords tied to the account).
- Each `Calendar` maps to a CalDAV collection; each `Event` (by `Uid`) to a `.ics`
  resource with an `ETag`; collection `ctag` / sync-token drives incremental sync.

### 4.6 Background jobs (Quartz.NET)

- **Reminder dispatch** — scan upcoming occurrences, send due reminders via email /
  Web Push, log to `NotificationLog` for idempotency.
- **Recurrence horizon / cleanup** — housekeeping, expired token cleanup, etc.
- Quartz persists to the primary DB so jobs survive restarts.

---

## 5. Frontend Architecture

- **React + TypeScript + Vite** SPA.
- **FullCalendar** for month/week/day/agenda views (drag-drop, resize).
- **Generated TypeScript API client from OpenAPI** — types always match the backend;
  wrapped in a data-fetching layer (React Query recommended in design phase).
- Auth: store JWT, attach as bearer; guard routes; refresh flow.
- **Web Push**: service worker + VAPID public key; subscription registered to backend.
- State: server state via the query layer; light local UI state only.
- Feature areas: auth, calendar list/management, event editor (incl. recurrence &
  reminder UI), import/export, settings (timezone, notification prefs, app passwords).

---

## 6. Data Storage & Configuration

- **EF Core** with two providers: **PostgreSQL** (primary) and **SQLite** (fallback).
  One model, provider chosen at startup from configuration.
- **Everything configured via environment variables** (12-factor), e.g.:
  - `DATABASE_PROVIDER` = `Postgres` | `Sqlite`
  - `POSTGRES_CONNECTION` (host, port, db, user, password)
  - `APPDATA_PATH` = `/appdata` (SQLite file, uploaded .ics, VAPID keys, etc.)
  - `SMTP_HOST/PORT/USER/PASSWORD/FROM`
  - `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` / `VAPID_SUBJECT`
  - `JWT_SIGNING_KEY`, `JWT_ISSUER`, `JWT_AUDIENCE`
  - `PUBLIC_BASE_URL` (for links, CalDAV principal URLs, push)
  - `LOG_LEVEL` (global minimum) + `LOG_LEVEL__<Namespace>` overrides; `LOG_FORMAT` = `json` | `console`
- Persistent data lives under the **`/appdata`** volume so the container is disposable.
- EF Core **migrations** applied on startup (guarded) or via an init step.

---

## 7. Logging & Observability

Modern, structured logging is a first-class requirement — not `Console.WriteLine`.

- **Serilog** as the logging provider, wired into the ASP.NET Core `ILogger<T>`
  abstraction so app code depends only on `ILogger`, not on Serilog directly.
- **Structured / semantic logs** — log events with named properties
  (`log.Information("Reminder sent for {EventId} to {UserId}", ...)`), not string
  concatenation, so logs are queryable.
- **Two output modes** (via `LOG_FORMAT`):
  - `json` — structured JSON to stdout, the container/production default (12-factor) so
    the operator's log stack (Loki, ELK, Seq, cloud) can ingest it.
  - `console` — **colorful, human-readable console output** for local dev: level-colored
    lines (e.g. Info green, Warning yellow, Error red), highlighted timestamps, and
    color-emphasized structured properties. Serilog's themed console sink
    (`Serilog.Sinks.Console` with an ANSI theme) drives this; auto-detects and disables
    color when the output isn't a TTY (piped/CI) so logs stay clean there.
- **Correlation / request IDs** — enrich every log with a request/trace id
  (Serilog request logging + `X-Correlation-Id` passthrough) so a single request can be
  followed across API → jobs. Include user id where available.
- **Configurable levels via env vars** — global minimum level plus per-namespace
  overrides (e.g. quiet EF Core SQL in prod, verbose in dev) without a rebuild.
- **Sensitive-data hygiene** — never log passwords, JWTs, push secrets, or full event
  bodies; redact tokens and PII. This is a review checklist item.
- **Scope-aware areas** — meaningful logs around auth, CalDAV sync operations
  (per DAVx⁵ interaction), reminder dispatch, iCal import/export, and background jobs,
  since those are the parts most likely to need field debugging.
- **Health & readiness endpoints** (`/health`, `/ready`) for the proxy / orchestrator.
- **Optional (post-v1):** OpenTelemetry traces + metrics — Serilog and the OTel .NET SDK
  interoperate, so the seam is left open. Not required for v1.

## 8. Deployment

- Ships as **Docker** images: backend, frontend (static build served by the backend or
  a small static server), and Postgres (compose service; external Postgres also allowed).
- The app **exposes plain HTTP** and is **reverse-proxy-agnostic** — the operator runs
  their proxy of choice (Caddy / Traefik / nginx / …) to terminate **TLS**. TLS matters
  because DAVx⁵ effectively requires HTTPS.
- Backend uses ForwardedHeaders middleware and `PUBLIC_BASE_URL` so generated URLs are
  correct behind the proxy.
- A sample `docker-compose.yml` will be provided (app + Postgres + named `appdata`
  volume), without an opinionated proxy — documented as "bring your own".

---

## 9. Key Risks & Open Questions

1. **CalDAV library choice (highest risk).** Needs a spike to pick and validate against
   real DAVx⁵ sync. Determines a lot of the CalDAV project shape.
2. **CalDAV auth for DAVx⁵.** JWT won't work for DAVx⁵ → design **app passwords** /
   Basic-over-TLS as a distinct credential path.
3. **Recurrence correctness** across DST and edit modes (this / this-and-future / all) —
   requires a strong test suite; align our model with iCal semantics via Ical.Net.
4. **iCal round-trip fidelity** (import → store → export → CalDAV) — one canonical
   serialization path to avoid divergence.
5. ~~Refresh-token strategy~~ — **resolved:** access + rotating refresh token, server-side
   refresh-token tracking (see §4.4). SPA token storage detail decided in Phase 1/2.
6. **Web Push** browser support & VAPID key lifecycle/rotation.

---

## 10. Proposed Build Phases (draft)

1. **Foundations** — repo structure, Docker skeleton, EF Core + dual provider, Identity
   + JWT, OpenAPI, generated FE client, empty React shell, **Serilog structured logging
   + health endpoints** wired in from day one.
   - ✅ *Done:* solution skeleton (host + 6 libs incl. two provider migration projects),
     Serilog (colour/JSON) + `/health` + `/ready`, template cleanup, OpenApi advisory fix.
   - ✅ *Done:* EF Core dual provider (Postgres/SQLite) with **separate per-provider
     migration assemblies**, ASP.NET Core Identity (Guid keys), **JWT access + rotating
     refresh tokens** with reuse detection, startup migrations, DB readiness check.
     Verified end-to-end (register/login/refresh/reuse/logout). SQLitePCLRaw advisory fixed.
   - ✅ *Done:* React + Vite shell (`/web`), **openapi-typescript + openapi-fetch +
     TanStack Query** typed client generated from the live OpenAPI doc, FullCalendar views,
     working auth screen. Frontend typechecks + builds.
   - ✅ *Done:* Docker single-container build (SPA built + served from API `wwwroot`),
     `docker-compose.yml` (app + Postgres + `appdata`/`pgdata` volumes), `.env.example`,
     `.dockerignore`. ⚠️ *Image build not yet run* — Docker Desktop daemon was offline;
     files authored but `docker build` unverified.
2. **Core calendar** — Calendars + Events CRUD, timezone-correct storage, FullCalendar
   views, event editor.
   - ✅ *Done:* `Calendar` + `CalendarEvent` entities, per-user default calendar
     (auto-created), events REST API (`/api/events` GET/POST/PUT/DELETE, `[Authorize]`,
     range filter), EF migrations for both providers, frontend wired via TanStack Query
     (in-memory state replaced by real persistence — create/edit/delete/drag). Verified
     end-to-end incl. survival across restart.
   - **Note:** times stored as **UTC `DateTime`** (not `DateTimeOffset`) — SQLite can't
     `ORDER BY`/compare `DateTimeOffset` in SQL; `DateTimeOffset` is used only at the API
     boundary. Per-event IANA `TimeZoneId` column exists but full per-zone/DST handling is
     Phase 3.
3. **Recurrence** — RRULE store + expand-on-read, edit modes, exceptions; heavy tests.
4. **iCal import/export** — Ical.Net round-trip.
5. **Reminders** — Quartz.NET jobs, email (SMTP), Web Push (VAPID), reminder UI.
6. **CalDAV** — library spike, protocol endpoints, app-password auth, DAVx⁵ validation.
7. **Hardening & deploy** — sample compose, docs, env-var config, migrations on startup.

---

*This document is the living architecture reference. Decisions above are locked unless
revisited; open items in §8 are resolved during their build phase and recorded here.*
