#requires -Version 7
# Seeds the 24seven backend with a demo user, two calendars, and a set of categories,
# then fills the calendar with demo events that each take their colour from a category.
#
#   User       -> test@test.com / Test1234#1234 (registered if missing, otherwise logged in)
#   Calendars  -> "Personal" (the default) and "Work" (created if missing), so the
#                 calendar switcher has something to toggle.
#   Categories -> Work, Personal, Family, Health, Social, Finance, Travel. New accounts
#                 already have Work/Personal/Family (registration defaults) — those are
#                 reused; the rest are created. Each event references one; its colour comes
#                 from the category.
#   Events     -> from seed-events.json (shared with seed.sh), anchored to the FIRST DAY
#                 OF THE CURRENT MONTH, so the seeded calendar
#                 always looks current: weekly recurring series (standup, gym, team sync,
#                 sprint review, family dinner), all-day + multi-day events (birthday,
#                 conference, payday), and one-off appointments across this and next month.
#                 Work-ish events land in "Work", everything else in "Personal".
#
#   Tasks      -> due dates relative to today, priorities, undated tasks and subtasks.
#
# The backend must already be running (e.g. via ./deploy/dev.ps1).
#
# Usage:
#   ./deploy/seed.ps1                          # seed against http://localhost:5299
#   ./deploy/seed.ps1 -BaseUrl http://host:80  # seed a different instance
#   ./deploy/seed.ps1 -Force                   # seed even if the user already has events

param(
    [string]$BaseUrl  = 'http://localhost:5299',
    [string]$Email    = 'test@test.com',
    [string]$Password = 'Test1234#1234',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# ---------- auth ----------------------------------------------------------------------

$credentials = @{ email = $Email; password = $Password } | ConvertTo-Json

try {
    $tokens = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/register" `
        -ContentType 'application/json' -Body $credentials `
        -SkipHttpErrorCheck -StatusCodeVariable registerStatus
}
catch {
    throw "Cannot reach the backend at $BaseUrl. Start it first (./deploy/dev.ps1). ($_)"
}

if ($registerStatus -eq 200) {
    Write-Host "==> registered $Email" -ForegroundColor Green
}
else {
    # Most likely the user already exists — fall back to login.
    $tokens = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" `
        -ContentType 'application/json' -Body $credentials `
        -SkipHttpErrorCheck -StatusCodeVariable loginStatus
    if ($loginStatus -ne 200) {
        throw "Register failed and login failed too (register=$registerStatus, login=$loginStatus)."
    }
    Write-Host "==> $Email already exists, logged in" -ForegroundColor Green
}

$auth = @{ Authorization = "Bearer $($tokens.accessToken)" }

# ---------- calendars -----------------------------------------------------------------
# Listing bootstraps the default "Personal" calendar server-side; "Work" is ours to add.

$calendars = Invoke-RestMethod -Uri "$BaseUrl/api/calendars" -Headers $auth
$personal = $calendars | Where-Object { $_.name -eq 'Personal' -and -not $_.subscription } | Select-Object -First 1
if (-not $personal) {
    $personal = $calendars | Where-Object { $_.name -ne 'Work' -and -not $_.subscription } | Select-Object -First 1
}
if (-not $personal) { throw 'No writable personal calendar found.' }
$personalId = $personal.id

$work = @($calendars) | Where-Object { $_.name -eq 'Work' } | Select-Object -First 1
if (-not $work) {
    $work = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/calendars" -Headers $auth `
        -ContentType 'application/json' -Body (@{ name = 'Work' } | ConvertTo-Json)
    Write-Host "==> created 'Work' calendar" -ForegroundColor Green
}
$workId = [string]$work.id

# ---------- categories ----------------------------------------------------------------
# New accounts start with default categories (Work, Personal, Family, Important); we reuse
# any that already exist and create the rest. Colours are exact CSS3-named values (app
# swatch convention, lossless for the iCalendar COLOR property). Each event takes its
# colour from its category.

$categoryColors = [ordered]@{
    Work     = '#6495ED'  # cornflowerblue
    Personal = '#3CB371'  # mediumseagreen
    Family   = '#DAA520'  # goldenrod
    Health   = '#FF6347'  # tomato
    Social   = '#BA55D3'  # mediumorchid
    Finance  = '#FFD700'  # gold
    Travel   = '#40E0D0'  # turquoise
}

$categoryId = @{}
$existingCategories = Invoke-RestMethod -Uri "$BaseUrl/api/categories" -Headers $auth
foreach ($c in $existingCategories) { $categoryId[[string]$c.name] = [string]$c.id }
foreach ($name in $categoryColors.Keys) {
    if (-not $categoryId.ContainsKey($name)) {
        $created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/categories" -Headers $auth `
            -ContentType 'application/json' -Body (@{ name = $name; color = $categoryColors[$name] } | ConvertTo-Json)
        $categoryId[$name] = $created.id
        Write-Host "==> created category '$name'" -ForegroundColor Green
    }
}

# ---------- date helpers --------------------------------------------------------------

# The IANA time zone the events are authored in (backend expands recurrences DST-correct).
$timeZone = 'UTC'
if ([TimeZoneInfo]::Local.HasIanaId) {
    $timeZone = [TimeZoneInfo]::Local.Id
}
else {
    $iana = $null
    if ([TimeZoneInfo]::TryConvertWindowsIdToIanaId([TimeZoneInfo]::Local.Id, [ref]$iana)) {
        $timeZone = $iana
    }
}

$monthStart = (Get-Date).Date.AddDays(1 - (Get-Date).Day)

# First occurrence of a weekday on or after $from.
function First-Dow([datetime]$from, [DayOfWeek]$dow) {
    $d = $from.Date
    while ($d.DayOfWeek -ne $dow) { $d = $d.AddDays(1) }
    return $d
}

# Local wall-clock time on a given day -> UTC ISO instant (per-date DST handled by .NET).
function UtcIso([datetime]$day, [int]$hour, [int]$minute = 0) {
    $local = [DateTime]::SpecifyKind($day.Date.AddHours($hour).AddMinutes($minute), 'Local')
    return $local.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

# All-day events use date-at-UTC-midnight; end is the INCLUSIVE last day (API convention).
function AllDayIso([datetime]$day) {
    return $day.ToString('yyyy-MM-dd') + 'T00:00:00Z'
}

# ---------- tasks ----------------------------------------------------------------------
$tasks = @(Invoke-RestMethod -Uri "$BaseUrl/api/tasks?includeDone=true" -Headers $auth | Write-Output)
$today = (Get-Date).Date
foreach ($spec in (Get-Content -Raw (Join-Path $PSScriptRoot 'seed-tasks.json') | ConvertFrom-Json)) {
    $calendar = if ($spec.calendar -eq 'Work') { $workId } else { $personalId }
    $inCalendar = @($tasks | Where-Object { $_.calendarId -eq $calendar })
    $parentId = $null
    if ($spec.parent) {
        $parent = $inCalendar | Where-Object { $_.title -eq $spec.parent -and -not $_.parentId } | Select-Object -First 1
        if (-not $parent) { throw "Missing parent task: $($spec.parent)" }
        $parentId = $parent.id
    }
    if ($inCalendar | Where-Object { $_.title -eq $spec.title -and $_.parentId -eq $parentId }) { continue }
    $body = @{ calendarId = $calendar; title = $spec.title; priority = $spec.priority; parentId = $parentId }
    if ($null -ne $spec.days) {
        $body.due = AllDayIso ($today.AddDays($spec.days))
        $body.dueAllDay = $true
    }
    $tasks += Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/tasks" -Headers $auth `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    Write-Host "    + task: $($spec.title)"
}

# ---------- idempotency guard ---------------------------------------------------------

$existing = Invoke-RestMethod -Uri "$BaseUrl/api/events" -Headers $auth -Body @{
    from = AllDayIso $monthStart
    to   = AllDayIso $monthStart.AddMonths(3)
}
if ($existing.Count -gt 0 -and -not $Force) {
    Write-Host "==> $Email already has $($existing.Count) event(s) from $($monthStart.ToString('yyyy-MM')) onward — skipping event creation." -ForegroundColor Yellow
    Write-Host "    Re-run with -Force to seed anyway (will create duplicates)." -ForegroundColor Yellow
    exit 0
}

# ---------- create --------------------------------------------------------------------
# The events live in seed-events.json, shared with seed.sh. Each names its calendar and
# category (its colour comes from the category) and a day relative to the first of this or
# next month: "plusDays" on from there, then the first "weekday" (1=Mon..7=Sun) on or after.
# Timed events give local wall-clock "start"/"end" times; all-day ones span "days" days.

$nextMonth = $monthStart.AddMonths(1)
$events = @(Get-Content -Raw (Join-Path $PSScriptRoot 'seed-events.json') | ConvertFrom-Json)

function Fixture-Day($on) {
    $d = if ($on.from -eq 'nextMonth') { $nextMonth } else { $monthStart }
    $d = $d.AddDays([int]($on.plusDays ?? 0))
    # ISO weekday 1=Mon..7=Sun → .NET DayOfWeek (Sunday = 0).
    if ($null -ne $on.weekday) { $d = First-Dow $d ([DayOfWeek]([int]$on.weekday % 7)) }
    return $d
}

function Local-Time([datetime]$day, [string]$hhmm) {
    $parts = $hhmm.Split(':')
    return UtcIso $day ([int]$parts[0]) ([int]$parts[1])
}

Write-Host "==> seeding $($events.Count) events (anchor: $($monthStart.ToString('yyyy-MM-dd')), tz: $timeZone)" -ForegroundColor Cyan

foreach ($e in $events) {
    $day = Fixture-Day $e.on
    $allDay = [bool]($e.allDay ?? $false)
    if ($allDay) {
        # All-day: the date at UTC midnight; the end is the INCLUSIVE last day (API convention).
        $start = AllDayIso $day
        $days = [int]($e.days ?? 1)
        $end = if ($days -gt 1) { AllDayIso $day.AddDays($days - 1) } else { $null }
    }
    else {
        $start = Local-Time $day $e.start
        $end = Local-Time $day $e.end
    }
    $body = @{
        calendarId  = if ($e.calendar -eq 'Work') { $workId } else { $personalId }
        title       = $e.title
        description = $e.description ?? $null
        location    = $e.location ?? $null
        categoryId  = $categoryId[$e.category]
        start       = $start
        end         = $end
        allDay      = $allDay
        recurrence  = $e.recurrence ?? $null
        timeZone    = $timeZone
        reminders   = @($e.reminders ?? @())
    } | ConvertTo-Json -Depth 4

    $null = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/events" -Headers $auth `
        -ContentType 'application/json' -Body $body
    $kind = if ($e.recurrence) { 'recurring' } elseif ($allDay) { 'all-day' } else { 'one-off' }
    Write-Host ("    + {0,-24} ({1}, {2}, {3})" -f $e.title, $kind, $e.calendar, $e.category)
}

Write-Host "==> done. Demo calendar ready for $Email." -ForegroundColor Green
