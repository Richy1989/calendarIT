# Settings → About page

## Goal

Add an **About** section to the Settings screen listing the project's name and license, plus
the major open-source libraries used and their licenses. Must match the existing settings style
(cards, rows, badges) — no new visual language.

## Placement

- New `about` value in the `Section` union in `web/src/SettingsPage.tsx`.
- New sidebar nav button (info/ⓘ icon), placed after **Sync** / near the bottom of the nav.
- Rendered by a new `AboutSection` component in the same file.

## Content

### Card 1 — CalendarIT (the project)

- Name: **CalendarIT**
- One-line description: "A self-hosted calendar."
- License line: **MIT License · © 2026 Richy Leopold**
- Link to source: `https://github.com/Richy1989/calendarIT`
- No version number (web package version is `0.0.0`; not meaningful).

### Card 2 — Open-source libraries

Curated **static** list in code (`const LIBRARIES`), split into two groups with subheads:

**Frontend**
- React — MIT — https://react.dev
- FullCalendar — MIT — https://fullcalendar.io
- TanStack Query — MIT — https://tanstack.com/query
- openapi-fetch — MIT — https://openapi-ts.dev/openapi-fetch/
- Vite — MIT — https://vite.dev
- Inter, JetBrains Mono & Space Grotesk (fonts) — SIL OFL 1.1 — https://fontsource.org

**Backend**
- .NET / ASP.NET Core — MIT — https://dotnet.microsoft.com
- Entity Framework Core — MIT — https://learn.microsoft.com/ef/core/
- Npgsql (EF Core provider) — PostgreSQL License — https://www.npgsql.org
- ASP.NET Core Identity — MIT — https://learn.microsoft.com/aspnet/core/security/authentication/identity
- Ical.Net — MIT — https://github.com/ical-org/ical.net
- Quartz.NET — Apache-2.0 — https://www.quartz-scheduler.net
- MailKit — MIT — https://github.com/jstedfast/MailKit
- Serilog — Apache-2.0 — https://serilog.net
- WebPush — MPL-2.0 — https://github.com/web-push-libs/web-push-csharp

Each row: library name · license tag (small pill) · external link ↗ (opens new tab,
`rel="noreferrer"`).

## Styling

Reuse existing settings primitives. Add a small amount of CSS to `web/src/index.css` (or wherever
the settings styles live):
- `.license-badge` — small pill tag for the license name.
- `.lib-group` / `.lib-row` — the grouped list layout (name left, badge + link right).

## Out of scope

- No auto-generation from manifests (transitive deps excluded — "major" libraries only).
- No full license texts inline.
- No backend/API changes — frontend-only.
