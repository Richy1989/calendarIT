# Local Reminder Notification Fallback — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver real desktop notifications for "Browser"-channel reminders in browsers that can't or won't use a push service (Brave with FCM off, non-HTTPS, old Safari), by polling a new read-only endpoint while the app is open — no Google, all modern browsers.

**Architecture:** Reuse the existing `WebPush` reminder channel. Each browser runs in one mode: **push** (real subscription, backend delivers) or **local** (no subscription — the app polls `GET /api/reminders/due` every ~30s and shows notifications itself). A new backend query service reuses the recurrence/window math already in `ReminderDispatchJob` (extracted into a shared helper). No database migration, no new channel.

**Tech Stack:** Backend — ASP.NET Core (.NET 10 preview), EF Core, Ical.Net, xUnit. Frontend — React 19 / Vite / TS 6, openapi-fetch, service worker Notification API, Vitest (added here).

## Global Constraints

- Timestamps are UTC `DateTime` internally; `DateTimeOffset` only at the API boundary. Convert inbound `.UtcDateTime`, outbound `new DateTimeOffset(DateTime.SpecifyKind(x, DateTimeKind.Utc))`.
- Build/test against Release to dodge the dev-app DLL lock: `dotnet build -c Release`, `dotnet test -c Release` (run from `core/calendarITCore/`).
- `web/.npmrc` sets `legacy-peer-deps` — keep it; npm installs must honor it.
- `npm run gen:api` regenerates `web/src/api/schema.d.ts` from the LIVE backend OpenAPI — the backend must be running on `http://localhost:5299` first.
- Canonical dev port is 5299.
- No database migration is part of this feature. If you find yourself writing one, stop — you've diverged from the design.
- Reminder body copy mirrors the existing push path: `"Starts at <time>."` plus a `Location:` line when the event has a location.

---

### Task 1: Extract the reminder occurrence-window helper (backend refactor)

`ReminderDispatchJob` has a private `OccurrencesInWindow` + `Truncate` that the new due-query needs too. Extract them into a shared static class so both use one copy. Existing `ReminderDispatchTests` are the regression net; add one focused test for the helper.

**Files:**
- Create: `core/calendarITCore/CalendarIT.Infrastructure/Notifications/ReminderOccurrences.cs`
- Modify: `core/calendarITCore/CalendarIT.Infrastructure/Notifications/ReminderDispatchJob.cs` (remove the private `OccurrencesInWindow` and `Truncate`; call the shared helper)
- Test: `core/calendarITCore/CalendarIT.Tests/ReminderOccurrencesTests.cs`

**Interfaces:**
- Produces:
  - `ReminderOccurrences.InWindow(CalendarEvent ev, DateTime occFrom, DateTime occTo) : IEnumerable<DateTime>` — occurrence starts (truncated to whole seconds, UTC) with `occFrom < start <= occTo`. Handles one-off (`RRule == null`) and recurring events.
  - `ReminderOccurrences.Truncate(DateTime dt) : DateTime` — floors to the second, `Kind = Utc`.

- [ ] **Step 1: Write the failing test**

```csharp
// core/calendarITCore/CalendarIT.Tests/ReminderOccurrencesTests.cs
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Notifications;

namespace CalendarIT.Tests;

public sealed class ReminderOccurrencesTests
{
    private static CalendarEvent Event(DateTime startUtc, string? rrule = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Dentist",
        StartUtc = startUtc,
        EndUtc = startUtc.AddMinutes(30),
        RRule = rrule,
        TimeZoneId = "UTC",
    };

    [Fact]
    public void OneOff_InsideWindow_IsReturned()
    {
        var ev = Event(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var hits = ReminderOccurrences.InWindow(
            ev, new DateTime(2026, 9, 1, 8, 59, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), Assert.Single(hits));
    }

    [Fact]
    public void OneOff_OutsideWindow_IsNotReturned()
    {
        var ev = Event(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var hits = ReminderOccurrences.InWindow(
            ev, new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 1, 0, DateTimeKind.Utc));
        Assert.Empty(hits); // start == occFrom is excluded (window is exclusive-left)
    }

    [Fact]
    public void Recurring_ReturnsTheOccurrenceInWindow_NotTheSeriesStart()
    {
        var ev = Event(new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc), rrule: "FREQ=DAILY");
        var hits = ReminderOccurrences.InWindow(
            ev, new DateTime(2026, 9, 1, 8, 59, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), Assert.Single(hits));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReminderOccurrences"` (from `core/calendarITCore/`)
Expected: FAIL to compile — `ReminderOccurrences` does not exist.

- [ ] **Step 3: Create the shared helper**

```csharp
// core/calendarITCore/CalendarIT.Infrastructure/Notifications/ReminderOccurrences.cs
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// The occurrence starts of an event whose (recurrence-expanded) start falls in a half-open
/// window (occFrom, occTo]. Shared by the reminder dispatch job and the due-reminder query so the
/// window arithmetic lives in exactly one place.
/// </summary>
public static class ReminderOccurrences
{
    public static IEnumerable<DateTime> InWindow(CalendarEvent ev, DateTime occFrom, DateTime occTo)
    {
        if (ev.RRule is null)
        {
            if (ev.StartUtc > occFrom && ev.StartUtc <= occTo)
            {
                yield return Truncate(ev.StartUtc);
            }
            yield break;
        }

        var end = ev.EndUtc ?? ev.StartUtc.AddHours(1);
        var exDates = RecurrenceExpander.ParseExDates(ev.ExDates);
        foreach (var occ in RecurrenceExpander.Expand(ev.StartUtc, end, ev.TimeZoneId, ev.RRule, exDates, occFrom, occTo.AddSeconds(1)))
        {
            if (occ.StartUtc > occFrom && occ.StartUtc <= occTo)
            {
                yield return Truncate(occ.StartUtc);
            }
        }
    }

    public static DateTime Truncate(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
```

- [ ] **Step 4: Point the job at the helper**

In `ReminderDispatchJob.cs`: delete the private `OccurrencesInWindow` method (lines ~131–151) and the private `Truncate` (lines ~255–256). Replace the two call sites:
- In `RunAsync`, `Truncate(timeProvider.GetUtcNow().UtcDateTime)` → `ReminderOccurrences.Truncate(timeProvider.GetUtcNow().UtcDateTime)`.
- In the `due` loop, `OccurrencesInWindow(reminder.Event!, windowStart + offset, now + offset)` → `ReminderOccurrences.InWindow(reminder.Event!, windowStart + offset, now + offset)`.

(The `using CalendarIT.Infrastructure.Calendars;` in the job may now be unused — remove it only if the compiler warns.)

- [ ] **Step 5: Run tests to verify all pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReminderOccurrences|FullyQualifiedName~ReminderDispatch"`
Expected: PASS (new helper tests + all existing dispatch regression tests).

- [ ] **Step 6: Commit**

```bash
git add core/calendarITCore/CalendarIT.Infrastructure/Notifications/ReminderOccurrences.cs \
        core/calendarITCore/CalendarIT.Infrastructure/Notifications/ReminderDispatchJob.cs \
        core/calendarITCore/CalendarIT.Tests/ReminderOccurrencesTests.cs
git commit -m "refactor: extract shared ReminderOccurrences window helper"
```

---

### Task 2: Due-reminder query service (backend)

The read-only query behind the endpoint. Returns the user's `WebPush`-channel reminder occurrences whose trigger falls in `(sinceUtc, now]`, with `sinceUtc` clamped to no earlier than `now − 1h`.

**Files:**
- Create: `core/calendarITCore/CalendarIT.Application/Notifications/DueReminderModels.cs`
- Create: `core/calendarITCore/CalendarIT.Application/Notifications/IDueReminderQuery.cs`
- Create: `core/calendarITCore/CalendarIT.Infrastructure/Notifications/DueReminderQuery.cs`
- Test: `core/calendarITCore/CalendarIT.Tests/DueReminderQueryTests.cs`

**Interfaces:**
- Consumes: `ReminderOccurrences.InWindow`, `ReminderOccurrences.Truncate` (Task 1).
- Produces:
  - `DueReminderDto(Guid ReminderId, DateTimeOffset OccurrenceStartUtc, string Title, string? Location)`
  - `DueRemindersResponse(IReadOnlyList<DueReminderDto> Items)`
  - `IDueReminderQuery.GetDueAsync(Guid userId, DateTime sinceUtc, CancellationToken) : Task<IReadOnlyList<DueReminderDto>>`
  - `DueReminderQuery(AppDbContext db, TimeProvider clock)` — constructable directly in tests.

- [ ] **Step 1: Write the failing test**

```csharp
// core/calendarITCore/CalendarIT.Tests/DueReminderQueryTests.cs
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

public sealed class DueReminderQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly Guid _userId = Guid.NewGuid();
    private Guid _calendarId;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public DueReminderQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = _userId, UserName = "owner@test", NormalizedUserName = "OWNER@TEST",
            Email = "owner@test", NormalizedEmail = "OWNER@TEST",
        });
        _db.SaveChanges();
        _calendarId = DefaultCalendar.GetOrCreateAsync(_db, _clock, _userId).GetAwaiter().GetResult().Id;
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private async Task AddReminderAsync(DateTime startUtc, int minutesBefore,
        ReminderChannel channel = ReminderChannel.WebPush, Guid? calendarId = null, string? rrule = null)
    {
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = calendarId ?? _calendarId,
            Uid = $"{Guid.NewGuid():N}@calendarit", Title = "Dentist",
            StartUtc = startUtc, EndUtc = startUtc.AddMinutes(30), RRule = rrule, TimeZoneId = "UTC",
            CreatedAt = startUtc,
            Reminders = [new Reminder { Id = Guid.NewGuid(), MinutesBefore = minutesBefore, Channel = channel }],
        });
        await _db.SaveChangesAsync();
    }

    private DueReminderQuery Query() => new(_db, _clock);

    [Fact]
    public async Task ReturnsAWebPushReminderTriggeringInsideTheWindow()
    {
        // now = 08:00; start 08:30, lead 30 → trigger 08:00, inside (07:55, 08:00].
        await AddReminderAsync(new DateTime(2026, 9, 1, 8, 30, 0), 30);
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        var item = Assert.Single(items);
        Assert.Equal("Dentist", item.Title);
    }

    [Fact]
    public async Task ExcludesEmailChannelReminders()
    {
        await AddReminderAsync(new DateTime(2026, 9, 1, 8, 30, 0), 30, channel: ReminderChannel.Email);
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ExcludesRemindersTriggeringInTheFuture()
    {
        // start 09:30, lead 30 → trigger 09:00, after now.
        await AddReminderAsync(new DateTime(2026, 9, 1, 9, 30, 0), 30);
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ClampsSinceToOneHourAgo()
    {
        // A trigger 90 min ago must NOT come back even if the client asks since yesterday.
        await AddReminderAsync(new DateTime(2026, 9, 1, 7, 0, 0), 30); // trigger 06:30, 90 min before now
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ScopesToTheRequestingUser()
    {
        var otherUser = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser
        {
            Id = otherUser, UserName = "other@test", NormalizedUserName = "OTHER@TEST",
            Email = "other@test", NormalizedEmail = "OTHER@TEST",
        });
        await _db.SaveChangesAsync();
        var otherCalendar = await DefaultCalendar.GetOrCreateAsync(_db, _clock, otherUser);
        await AddReminderAsync(new DateTime(2026, 9, 1, 8, 30, 0), 30, calendarId: otherCalendar.Id);

        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~DueReminderQuery"`
Expected: FAIL to compile — `DueReminderQuery` / DTOs don't exist.

- [ ] **Step 3: Create the models and interface**

```csharp
// core/calendarITCore/CalendarIT.Application/Notifications/DueReminderModels.cs
namespace CalendarIT.Application.Notifications;

/// <summary>One reminder occurrence that has come due, for the client to show locally.</summary>
public sealed record DueReminderDto(Guid ReminderId, DateTimeOffset OccurrenceStartUtc, string Title, string? Location);

/// <summary>The due-reminders poll response.</summary>
public sealed record DueRemindersResponse(IReadOnlyList<DueReminderDto> Items);
```

```csharp
// core/calendarITCore/CalendarIT.Application/Notifications/IDueReminderQuery.cs
namespace CalendarIT.Application.Notifications;

/// <summary>
/// Read-only: the signed-in user's browser-channel reminder occurrences whose trigger time falls
/// in (sinceUtc, now]. Used by the local-notification fallback poller. Never writes NotificationLog.
/// </summary>
public interface IDueReminderQuery
{
    Task<IReadOnlyList<DueReminderDto>> GetDueAsync(Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Implement the query**

```csharp
// core/calendarITCore/CalendarIT.Infrastructure/Notifications/DueReminderQuery.cs
using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Mirrors <see cref="ReminderDispatchJob"/>'s window arithmetic, but read-only and per-user, and
/// only for <see cref="ReminderChannel.WebPush"/> reminders (Email is delivered by the job, not here).
/// </summary>
public sealed class DueReminderQuery(AppDbContext db, TimeProvider clock) : IDueReminderQuery
{
    private static readonly TimeSpan MaxLookback = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<DueReminderDto>> GetDueAsync(
        Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var now = ReminderOccurrences.Truncate(clock.GetUtcNow().UtcDateTime);
        var floor = now - MaxLookback;
        var windowStart = sinceUtc < floor ? floor : ReminderOccurrences.Truncate(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc));

        var longestOffset = await db.Reminders
            .Where(r => r.Channel == ReminderChannel.WebPush && r.Event!.Calendar!.OwnerUserId == userId)
            .Select(r => (int?)r.MinutesBefore)
            .MaxAsync(cancellationToken) ?? 0;
        var horizon = now.AddMinutes(longestOffset);

        var reminders = await db.Reminders
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(r => r.Channel == ReminderChannel.WebPush
                && r.Event!.Calendar!.OwnerUserId == userId
                && (r.Event!.RRule != null
                    || (r.Event!.StartUtc > windowStart && r.Event!.StartUtc <= horizon)))
            .ToListAsync(cancellationToken);

        var items = new List<DueReminderDto>();
        foreach (var reminder in reminders)
        {
            var offset = TimeSpan.FromMinutes(reminder.MinutesBefore);
            foreach (var occStart in ReminderOccurrences.InWindow(reminder.Event!, windowStart + offset, now + offset))
            {
                items.Add(new DueReminderDto(
                    reminder.Id,
                    new DateTimeOffset(DateTime.SpecifyKind(occStart, DateTimeKind.Utc)),
                    reminder.Event!.Title,
                    reminder.Event!.Location));
            }
        }
        return items;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~DueReminderQuery"`
Expected: PASS (all five).

- [ ] **Step 6: Commit**

```bash
git add core/calendarITCore/CalendarIT.Application/Notifications/DueReminderModels.cs \
        core/calendarITCore/CalendarIT.Application/Notifications/IDueReminderQuery.cs \
        core/calendarITCore/CalendarIT.Infrastructure/Notifications/DueReminderQuery.cs \
        core/calendarITCore/CalendarIT.Tests/DueReminderQueryTests.cs
git commit -m "feat: add per-user due-reminder query for local notifications"
```

---

### Task 3: Endpoint + DI registration (backend)

Expose the query at `GET /api/reminders/due` and register the service.

**Files:**
- Create: `core/calendarITCore/calendarITCore/Controllers/RemindersController.cs`
- Modify: `core/calendarITCore/CalendarIT.Infrastructure/DependencyInjection.cs` (register `IDueReminderQuery`)

**Interfaces:**
- Consumes: `IDueReminderQuery` (Task 2), `User.GetUserId()` from `calendarITCore.Extensions`.
- Produces: HTTP `GET /api/reminders/due?sinceUtc=<ISO-8601>` → `DueRemindersResponse` (200). This is the endpoint the frontend `api/reminders.ts` (Task 6) consumes; its OpenAPI shape drives the generated `schema.d.ts`.

- [ ] **Step 1: Register the service in DI**

In `DependencyInjection.cs`, right after the push-subscription registration (line ~103), add:

```csharp
        // Read-only per-user due-reminder query for the local-notification fallback poller.
        services.AddScoped<IDueReminderQuery, DueReminderQuery>();
```

- [ ] **Step 2: Add the controller**

```csharp
// core/calendarITCore/calendarITCore/Controllers/RemindersController.cs
using calendarITCore.Extensions;
using CalendarIT.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>
/// Reminder occurrences that have just come due for the signed-in user, for browsers using the
/// local-notification fallback (no push service). Read-only; the reminder job still owns delivery
/// for push- and email-channel reminders.
/// </summary>
[ApiController]
[Route("api/reminders")]
[Authorize]
public sealed class RemindersController(IDueReminderQuery query) : ControllerBase
{
    /// <summary>
    /// Browser-channel reminders whose trigger fell in (sinceUtc, now]. sinceUtc is clamped
    /// server-side to no earlier than one hour ago; omit it to default to one hour ago.
    /// </summary>
    [HttpGet("due")]
    [ProducesResponseType<DueRemindersResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DueRemindersResponse>> Due(
        [FromQuery] DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
    {
        var since = sinceUtc?.UtcDateTime ?? DateTime.MinValue; // service clamps to now - 1h
        var items = await query.GetDueAsync(User.GetUserId(), since, cancellationToken);
        return Ok(new DueRemindersResponse(items));
    }
}
```

- [ ] **Step 3: Build and run the full backend test suite**

Run: `dotnet build -c Release && dotnet test -c Release` (from `core/calendarITCore/`)
Expected: build succeeds; all tests pass.

- [ ] **Step 4: Manual smoke of the endpoint**

Start the API (`cd calendarITCore && dotnet run`), then with a valid access token:
Run: `curl -s -H "Authorization: Bearer <token>" "http://localhost:5299/api/reminders/due"`
Expected: `{"items":[]}` (or due items). Confirms routing + auth.

- [ ] **Step 5: Commit**

```bash
git add core/calendarITCore/calendarITCore/Controllers/RemindersController.cs \
        core/calendarITCore/CalendarIT.Infrastructure/DependencyInjection.cs
git commit -m "feat: expose GET /api/reminders/due endpoint"
```

---

### Task 4: Frontend test runner + notify-mode decision (pure)

Add Vitest (no frontend test runner exists yet) and the pure mode-decision function, then wire mode persistence and a combined enable/disable into `webPush.ts`.

**Files:**
- Modify: `web/package.json` (add `vitest` devDependency + `"test"` script)
- Modify: `web/src/push/webPush.ts` (add `NotifyMode`, `decideNotifyMode`, mode persistence, `enableNotifications`, `disableNotifications`)
- Test: `web/src/push/webPush.test.ts`

**Interfaces:**
- Produces (consumed by Tasks 7, 8, 9):
  - `type NotifyMode = 'push' | 'local'`
  - `decideNotifyMode(opts: { permission: NotificationPermission; pushSupported: boolean; pushSubscribed: boolean }): NotifyMode | 'denied'`
  - `getNotifyMode(): NotifyMode | null`
  - `enableNotifications(): Promise<NotifyMode | 'denied' | 'unsupported'>`
  - `disableNotifications(): Promise<void>`
  - `isLocalNotifySupported(): boolean`

- [ ] **Step 1: Add Vitest**

Run (from `web/`): `npm install -D vitest@^3`
Then in `web/package.json` add to `scripts`: `"test": "vitest run"`.

- [ ] **Step 2: Write the failing test**

```ts
// web/src/push/webPush.test.ts
import { describe, expect, it } from 'vitest'
import { decideNotifyMode } from './webPush'

describe('decideNotifyMode', () => {
  it('is denied when permission is not granted', () => {
    expect(decideNotifyMode({ permission: 'denied', pushSupported: true, pushSubscribed: true })).toBe('denied')
    expect(decideNotifyMode({ permission: 'default', pushSupported: true, pushSubscribed: false })).toBe('denied')
  })

  it('is push when granted and a push subscription exists', () => {
    expect(decideNotifyMode({ permission: 'granted', pushSupported: true, pushSubscribed: true })).toBe('push')
  })

  it('falls back to local when granted but push could not subscribe', () => {
    expect(decideNotifyMode({ permission: 'granted', pushSupported: true, pushSubscribed: false })).toBe('local')
    expect(decideNotifyMode({ permission: 'granted', pushSupported: false, pushSubscribed: false })).toBe('local')
  })
})
```

- [ ] **Step 3: Run test to verify it fails**

Run (from `web/`): `npm test`
Expected: FAIL — `decideNotifyMode` is not exported.

- [ ] **Step 4: Implement in `webPush.ts`**

Add near the top of `web/src/push/webPush.ts`:

```ts
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
```

Then add the combined enable/disable at the end of the file. Reuse the existing subscribe steps; the key change is that a push-subscribe failure with permission granted resolves to local mode instead of an error:

```ts
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
```

Note: `ensurePushSubscribed` already calls `Notification.requestPermission()` internally; calling it after we've requested permission is harmless (it returns 'granted' immediately). Leave `ensurePushSubscribed`, `disablePush`, and `registerServiceWorker` in place.

- [ ] **Step 5: Run test to verify it passes**

Run (from `web/`): `npm test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add web/package.json web/package-lock.json web/src/push/webPush.ts web/src/push/webPush.test.ts
git commit -m "feat: notify-mode decision + push/local enable-disable"
```

---

### Task 5: Dedup + shown-store (pure)

The client-side dedup that prevents re-showing a reminder across polls, tabs, and refreshes.

**Files:**
- Create: `web/src/push/localReminderStore.ts`
- Test: `web/src/push/localReminderStore.test.ts`

**Interfaces:**
- Produces (consumed by Task 7):
  - `type DueItem = { reminderId: string; occurrenceStartUtc: string; title: string; location?: string | null }`
  - `type ShownEntry = { key: string; at: number }`
  - `selectToShow(shown: ShownEntry[], items: DueItem[], now: number): { toShow: DueItem[]; nextShown: ShownEntry[] }`
  - `RETENTION_MS: number`

- [ ] **Step 1: Write the failing test**

```ts
// web/src/push/localReminderStore.test.ts
import { describe, expect, it } from 'vitest'
import { RETENTION_MS, selectToShow, type ShownEntry } from './localReminderStore'

const item = (reminderId: string, occ: string) => ({
  reminderId, occurrenceStartUtc: occ, title: 'Dentist', location: null,
})

describe('selectToShow', () => {
  it('shows a brand-new item and records its key', () => {
    const { toShow, nextShown } = selectToShow([], [item('r1', '2026-09-01T08:00:00Z')], 1000)
    expect(toShow).toHaveLength(1)
    expect(nextShown.map((e) => e.key)).toContain('r1:2026-09-01T08:00:00Z')
  })

  it('suppresses an item already shown', () => {
    const shown: ShownEntry[] = [{ key: 'r1:2026-09-01T08:00:00Z', at: 900 }]
    const { toShow } = selectToShow(shown, [item('r1', '2026-09-01T08:00:00Z')], 1000)
    expect(toShow).toHaveLength(0)
  })

  it('does not show the same item twice within one batch', () => {
    const dup = item('r1', '2026-09-01T08:00:00Z')
    const { toShow } = selectToShow([], [dup, dup], 1000)
    expect(toShow).toHaveLength(1)
  })

  it('prunes keys older than the retention window', () => {
    const old: ShownEntry[] = [{ key: 'old:x', at: 0 }]
    const { nextShown } = selectToShow(old, [], RETENTION_MS + 1)
    expect(nextShown.find((e) => e.key === 'old:x')).toBeUndefined()
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run (from `web/`): `npm test`
Expected: FAIL — module doesn't exist.

- [ ] **Step 3: Implement the store**

```ts
// web/src/push/localReminderStore.ts
/* Pure dedup for local reminder notifications: decide which due items are new, and keep a pruned
 * record of what's been shown so a reminder isn't re-shown on the next poll, another tab, or a
 * refresh. Kept free of DOM/storage so it can be unit-tested directly. */

export type DueItem = {
  reminderId: string
  occurrenceStartUtc: string
  title: string
  location?: string | null
}

export type ShownEntry = { key: string; at: number }

/** How long a shown-key is remembered. Comfortably longer than the poll/lookback window. */
export const RETENTION_MS = 2 * 60 * 60 * 1000

const keyOf = (item: DueItem) => `${item.reminderId}:${item.occurrenceStartUtc}`

export function selectToShow(
  shown: ShownEntry[],
  items: DueItem[],
  now: number,
): { toShow: DueItem[]; nextShown: ShownEntry[] } {
  const have = new Set(shown.map((e) => e.key))
  const toShow: DueItem[] = []
  const added: ShownEntry[] = []
  for (const item of items) {
    const key = keyOf(item)
    if (!have.has(key)) {
      have.add(key)
      toShow.push(item)
      added.push({ key, at: now })
    }
  }
  const nextShown = [...shown, ...added].filter((e) => now - e.at < RETENTION_MS)
  return { toShow, nextShown }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run (from `web/`): `npm test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add web/src/push/localReminderStore.ts web/src/push/localReminderStore.test.ts
git commit -m "feat: pure dedup store for local reminder notifications"
```

---

### Task 6: API client for the due endpoint (frontend)

Regenerate the OpenAPI types (now that Task 3's endpoint exists) and add the typed fetch wrapper.

**Files:**
- Modify: `web/src/api/schema.d.ts` (regenerated — do not hand-edit)
- Create: `web/src/api/reminders.ts`

**Interfaces:**
- Consumes: `DueItem` shape from Task 5; the generated `paths`/`components` for `/api/reminders/due`.
- Produces: `getDueReminders(sinceUtc: string): Promise<DueItem[]>` (consumed by Task 7).

- [ ] **Step 1: Regenerate the schema**

Start the backend on 5299 (`cd core/calendarITCore/calendarITCore && dotnet run`), then from `web/`:
Run: `npm run gen:api`
Expected: `src/api/schema.d.ts` now contains a `"/api/reminders/due"` path and a `DueReminderDto` / `DueRemindersResponse` schema. Confirm with:
Run: `grep -c "reminders/due" src/api/schema.d.ts` → `1` or more.

- [ ] **Step 2: Add the API wrapper**

```ts
// web/src/api/reminders.ts
import { api } from './client'
import type { DueItem } from '../push/localReminderStore'

/** Due browser-channel reminders since the given ISO timestamp (server clamps to the last hour). */
export async function getDueReminders(sinceUtc: string): Promise<DueItem[]> {
  const { data, error } = await api.GET('/api/reminders/due', {
    params: { query: { sinceUtc } },
  })
  if (error || !data) return []
  return (data.items ?? []).map((i) => ({
    reminderId: String(i.reminderId),
    occurrenceStartUtc: String(i.occurrenceStartUtc),
    title: i.title,
    location: i.location ?? null,
  }))
}
```

- [ ] **Step 3: Type-check**

Run (from `web/`): `npm run build`
Expected: `tsc -b` passes (the generated types resolve the `/api/reminders/due` call). Fix any type mismatch surfaced here before continuing.

- [ ] **Step 4: Commit**

```bash
git add web/src/api/schema.d.ts web/src/api/reminders.ts
git commit -m "feat: typed client for due-reminders endpoint"
```

---

### Task 7: Local reminder poller (frontend)

The runtime piece: while in local mode, poll and show notifications via the service worker.

**Files:**
- Create: `web/src/push/localReminders.ts`

**Interfaces:**
- Consumes: `getDueReminders` (Task 6); `selectToShow`, `RETENTION_MS`, `DueItem`, `ShownEntry` (Task 5); `getNotifyMode` (Task 4).
- Produces (consumed by Tasks 8, 9): `startLocalReminderPoller(): void`, `stopLocalReminderPoller(): void`.

- [ ] **Step 1: Implement the poller**

```ts
// web/src/push/localReminders.ts
/* Local-mode fallback: while the app is open and this browser is in 'local' notify mode, poll the
 * backend for due browser-channel reminders and show them via the service worker. No push service
 * (no Google) is involved. Dedup lives in localReminderStore; this module owns timing + display. */

import { getDueReminders } from '../api/reminders'
import { getNotifyMode } from './webPush'
import { RETENTION_MS, selectToShow, type ShownEntry } from './localReminderStore'

const POLL_MS = 30_000
const SHOWN_STORAGE_KEY = 'calendarit.shownReminders'

let timer: number | null = null
let sinceUtc = ''

function loadShown(now: number): ShownEntry[] {
  try {
    const raw = JSON.parse(localStorage.getItem(SHOWN_STORAGE_KEY) ?? '[]') as ShownEntry[]
    return raw.filter((e) => e && typeof e.key === 'string' && now - e.at < RETENTION_MS)
  } catch {
    return []
  }
}

function saveShown(entries: ShownEntry[]): void {
  localStorage.setItem(SHOWN_STORAGE_KEY, JSON.stringify(entries))
}

function bodyFor(occurrenceStartUtc: string, location?: string | null): string {
  const when = new Date(occurrenceStartUtc).toLocaleString()
  return location ? `Starts at ${when}.\nLocation: ${location}` : `Starts at ${when}.`
}

async function pollOnce(): Promise<void> {
  if (getNotifyMode() !== 'local') return
  const now = Date.now()
  const items = await getDueReminders(sinceUtc)
  sinceUtc = new Date(now).toISOString()

  const { toShow, nextShown } = selectToShow(loadShown(now), items, now)
  saveShown(nextShown)
  if (toShow.length === 0) return

  const reg = await navigator.serviceWorker.ready
  for (const item of toShow) {
    reg.showNotification(`Reminder: ${item.title}`, {
      body: bodyFor(item.occurrenceStartUtc, item.location),
      tag: `${item.reminderId}:${item.occurrenceStartUtc}`,
      data: { url: '/' },
    })
  }
}

const onWake = () => { void pollOnce() }

/** Begin polling (idempotent). Safe to call on login and after enabling local mode. */
export function startLocalReminderPoller(): void {
  if (timer !== null) return
  sinceUtc = new Date().toISOString()
  timer = window.setInterval(onWake, POLL_MS)
  window.addEventListener('focus', onWake)
  void pollOnce()
}

/** Stop polling and detach listeners (idempotent). */
export function stopLocalReminderPoller(): void {
  if (timer !== null) {
    window.clearInterval(timer)
    timer = null
  }
  window.removeEventListener('focus', onWake)
}
```

- [ ] **Step 2: Type-check**

Run (from `web/`): `npm run build`
Expected: passes.

- [ ] **Step 3: Lint**

Run (from `web/`): `npm run lint`
Expected: no new errors in `localReminders.ts`.

- [ ] **Step 4: Commit**

```bash
git add web/src/push/localReminders.ts
git commit -m "feat: local reminder poller (no-Google notification fallback)"
```

---

### Task 8: Start/stop the poller from the app shell

Run the poller whenever the user is logged in and this browser is in local mode.

**Files:**
- Modify: `web/src/App.tsx` (add an effect keyed on `tokens`)

**Interfaces:**
- Consumes: `startLocalReminderPoller`, `stopLocalReminderPoller` (Task 7); `getNotifyMode` (Task 4).

- [ ] **Step 1: Wire the effect**

In `web/src/App.tsx`, add to the imports:

```ts
import { getNotifyMode } from './push/webPush'
import { startLocalReminderPoller, stopLocalReminderPoller } from './push/localReminders'
```

Then, alongside the existing `auth-expired` effect (after line ~62), add:

```tsx
  // While signed in on a browser that fell back to local notifications, poll for due reminders
  // and show them. Push-mode browsers are served by the backend job and don't poll.
  useEffect(() => {
    if (!tokens || getNotifyMode() !== 'local') {
      stopLocalReminderPoller()
      return
    }
    startLocalReminderPoller()
    return () => stopLocalReminderPoller()
  }, [tokens])
```

- [ ] **Step 2: Type-check and lint**

Run (from `web/`): `npm run build && npm run lint`
Expected: both pass. (An `oxlint` warning about the effect dependency array is acceptable — `getNotifyMode` reads localStorage, not reactive state; the settings toggle in Task 9 starts the poller directly on enable.)

- [ ] **Step 3: Commit**

```bash
git add web/src/App.tsx
git commit -m "feat: run local reminder poller while signed in"
```

---

### Task 9: Settings UI — reflect and control the mode

Update `NotificationsCard` to use the combined enable/disable, show which mode is active, and start/stop the poller immediately on toggle.

**Files:**
- Modify: `web/src/SettingsPage.tsx` (`NotificationsCard`)

**Interfaces:**
- Consumes: `enableNotifications`, `disableNotifications`, `getNotifyMode`, `isLocalNotifySupported`, `pushPermission` (Task 4); `startLocalReminderPoller`, `stopLocalReminderPoller` (Task 7).

- [ ] **Step 1: Update imports**

In `web/src/SettingsPage.tsx`, replace the existing push import line:

```ts
import { disablePush, ensurePushSubscribed, isPushSupported, pushPermission } from './push/webPush'
```

with:

```ts
import {
  disableNotifications, enableNotifications, getNotifyMode, isLocalNotifySupported,
  pushPermission, type NotifyMode,
} from './push/webPush'
import { startLocalReminderPoller, stopLocalReminderPoller } from './push/localReminders'
```

- [ ] **Step 2: Rewrite `NotificationsCard`**

Replace the whole `NotificationsCard` function (lines ~196–252) with:

```tsx
/**
 * Device-level notification control. Enabling tries real Web Push (delivers when the app is
 * closed); on a browser that can't subscribe (e.g. Brave with Google services off) it falls back
 * to local notifications that fire while CalendarIT is open. Per-browser.
 */
function NotificationsCard() {
  const supported = isLocalNotifySupported()
  const [mode, setMode] = useState<NotifyMode | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    setMode(getNotifyMode())
  }, [])

  const enabled = mode !== null

  const toggle = async (on: boolean) => {
    setBusy(true)
    setNotice(null)
    try {
      if (on) {
        const result = await enableNotifications()
        if (result === 'denied') {
          setNotice('Blocked. Allow notifications for this site in your browser settings.')
          setMode(null)
        } else if (result === 'unsupported') {
          setNotice("This browser can't show notifications.")
          setMode(null)
        } else {
          setMode(result)
          if (result === 'local') startLocalReminderPoller()
        }
      } else {
        await disableNotifications()
        stopLocalReminderPoller()
        setMode(null)
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="settings-card">
      <h2>Browser notifications</h2>
      <p className="settings-sub">
        Get reminders as desktop/phone notifications on this device. Choose “Browser” on an
        appointment’s reminder to use it.
      </p>
      {supported ? (
        <>
          <label className="toggle">
            <input type="checkbox" checked={enabled} disabled={busy} onChange={(e) => toggle(e.target.checked)} />
            <span>Enable notifications on this device</span>
          </label>
          {enabled && mode === 'local' && (
            <p className="settings-hint">
              This browser doesn’t support background push (or it’s turned off), so notifications
              work while CalendarIT is open in a tab or installed as an app.
            </p>
          )}
          {enabled && mode === 'push' && (
            <p className="settings-hint">Notifications work even when CalendarIT isn’t open.</p>
          )}
          {notice && <p className="settings-hint">{notice}</p>}
          {pushPermission() === 'denied' && !notice && (
            <p className="settings-hint">Notifications are blocked in your browser settings for this site.</p>
          )}
        </>
      ) : (
        <p className="settings-hint">This browser doesn’t support notifications (or the site isn’t on a secure origin).</p>
      )}
    </div>
  )
}
```

- [ ] **Step 3: Type-check and lint**

Run (from `web/`): `npm run build && npm run lint`
Expected: both pass. (`isPushSupported`, `ensurePushSubscribed`, `disablePush` remain exported from `webPush.ts` and are still used internally by `enableNotifications`/`disableNotifications`, so no dead-export errors.)

- [ ] **Step 4: Manual cross-browser smoke**

With backend + frontend running and a reminder set to "Browser" a couple of minutes out:
- **Chrome/Firefox:** enable notifications → hint says "even when CalendarIT isn’t open" (push mode). Reminder fires from the backend job.
- **Brave (Google services for push OFF):** enable → hint says "while CalendarIT is open" (local mode). Keep a tab open; the reminder fires within ~30s of its trigger. Enabling it in a second tab does not double-notify (same `tag`).

- [ ] **Step 5: Commit**

```bash
git add web/src/SettingsPage.tsx
git commit -m "feat: settings notifications reflect push vs local fallback mode"
```

---

## Self-Review

**Spec coverage:**
- Read-only `/api/reminders/due` with `sinceUtc` clamp + WebPush-channel filter + recurrence + per-user scope → Tasks 2, 3 (clamp/filter/scope tested in Task 2).
- Extract shared occurrence-window helper → Task 1.
- Frontend mode detection + persistence → Task 4.
- Local poller (interval + focus, SW `showNotification`) → Task 7, started app-wide → Task 8.
- Client dedup (tag + pruned localStorage set, pure function) → Task 5.
- Settings UI mode reflection + graceful Brave message → Task 9.
- No SW change (page calls `registration.showNotification`) → honored (no SW file touched).
- Cross-browser (Firefox push, Chromium push, Brave/others local) → Task 9 manual smoke; local path uses only universal APIs.
- No DB migration → honored (no migration task).
- Out-of-scope items (closed-app delivery, external services, new channel) → not present in any task.

**Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to Task N" — every step has concrete code or an exact command.

**Type consistency:** `DueReminderDto`/`DueRemindersResponse` (Task 2) match the controller return (Task 3) and the `data.items` shape mapped in `getDueReminders` (Task 6). `DueItem`/`ShownEntry`/`selectToShow`/`RETENTION_MS` (Task 5) match their uses in Tasks 6–7. `NotifyMode`/`getNotifyMode`/`enableNotifications`/`disableNotifications`/`isLocalNotifySupported` (Task 4) match Tasks 8–9. `startLocalReminderPoller`/`stopLocalReminderPoller` (Task 7) match Tasks 8–9.

**Note (deviation from spec testing):** The spec listed unit-testing "mode-selection logic". Realized as the pure `decideNotifyMode` (Task 4) — testable in Node without a DOM — rather than testing the storage/permission wrapper, which would require jsdom. This keeps Vitest a minimal, zero-config addition.
