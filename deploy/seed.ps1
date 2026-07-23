#requires -Version 7
# Seeds the CalendarIT backend with a demo user, two calendars, and a set of categories,
# then fills the calendar with demo events that each take their colour from a category.
#
#   User       -> test@test.com / Test1234#1234 (registered if missing, otherwise logged in)
#   Calendars  -> "Personal" (the default) and "Work" (created if missing), so the
#                 calendar switcher has something to toggle.
#   Categories -> Work, Personal, Family, Health, Social, Finance, Travel. New accounts
#                 already have Work/Personal/Family (registration defaults) — those are
#                 reused; the rest are created. Each event references one; its colour comes
#                 from the category.
#   Events     -> anchored to the FIRST DAY OF THE CURRENT MONTH, so the seeded calendar
#                 always looks current: weekly recurring series (standup, gym, team sync,
#                 sprint review, family dinner), all-day + multi-day events (birthday,
#                 conference, payday), and one-off appointments across this and next month.
#                 Work-ish events land in "Work", everything else in "Personal".
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
$personalId = [string](@($calendars)[0].id)

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

# ---------- idempotency guard ---------------------------------------------------------

$existing = Invoke-RestMethod -Uri "$BaseUrl/api/events" -Headers $auth -Body @{
    from = AllDayIso $monthStart
    to   = AllDayIso $monthStart.AddMonths(3)
}
if ($existing.Count -gt 0 -and -not $Force) {
    Write-Host "==> $Email already has $($existing.Count) event(s) from $($monthStart.ToString('yyyy-MM')) onward — nothing seeded." -ForegroundColor Yellow
    Write-Host "    Re-run with -Force to seed anyway (will create duplicates)." -ForegroundColor Yellow
    exit 0
}

# ---------- event definitions ---------------------------------------------------------
# Each event names a category (see above); its colour is inherited from that category.

$monday    = First-Dow $monthStart ([DayOfWeek]::Monday)
$tuesday   = First-Dow $monthStart ([DayOfWeek]::Tuesday)
$friday    = First-Dow $monthStart ([DayOfWeek]::Friday)
$sunday    = First-Dow $monthStart ([DayOfWeek]::Sunday)
$week2     = $monthStart.AddDays(7)
$week3     = $monthStart.AddDays(14)
$week4     = $monthStart.AddDays(21)
$nextMonth = $monthStart.AddMonths(1)
$confStart = First-Dow $week3 ([DayOfWeek]::Tuesday)

$events = @(
    # --- recurring series -------------------------------------------------------------
    @{ title = 'Team standup'; category = 'Work'; work = $true
       start = UtcIso $monday 9 15; end = UtcIso $monday 9 30
       recurrence = 'FREQ=WEEKLY;BYDAY=MO,WE,FR'; location = 'Zoom'
       description = 'Daily sync — what shipped, what''s next, blockers.'
       reminders = @(@{ minutesBefore = 10; channel = 'Email' }) }

    @{ title = 'Team sync'; category = 'Work'; work = $true
       start = UtcIso $monday 11 0; end = UtcIso $monday 12 0
       recurrence = 'FREQ=WEEKLY'; location = 'Room 2.04'
       description = 'Weekly planning and demos.' }

    @{ title = 'Gym'; category = 'Health'
       start = UtcIso $tuesday 18 0; end = UtcIso $tuesday 19 30
       recurrence = 'FREQ=WEEKLY;BYDAY=TU,TH'; location = 'FitOne' }

    @{ title = 'Sprint review'; category = 'Work'; work = $true
       start = UtcIso $friday 14 0; end = UtcIso $friday 15 0
       recurrence = 'FREQ=WEEKLY;INTERVAL=2'; location = 'Room 1.01'
       description = 'Demo, retro, next sprint scope.' }

    @{ title = 'Family dinner'; category = 'Family'
       start = UtcIso $sunday 18 0; end = UtcIso $sunday 20 0
       recurrence = 'FREQ=WEEKLY' }

    # --- all-day / multi-day ----------------------------------------------------------
    @{ title = "Mom's birthday"; category = 'Family'
       start = AllDayIso $monthStart.AddDays(11); allDay = $true
       reminders = @(@{ minutesBefore = 1440; channel = 'Email' }) }

    @{ title = 'DevConf'; category = 'Travel'; work = $true
       start = AllDayIso $confStart; end = AllDayIso $confStart.AddDays(2); allDay = $true
       location = 'Convention Center'
       description = 'Three days of talks and workshops.' }

    @{ title = 'Payday'; category = 'Finance'
       start = AllDayIso $monthStart.AddDays(27); allDay = $true }

    @{ title = 'Day off'; category = 'Personal'
       start = AllDayIso (First-Dow $nextMonth.AddDays(7) ([DayOfWeek]::Friday)); allDay = $true }

    # --- one-off appointments, this month ---------------------------------------------
    @{ title = 'Dentist'; category = 'Health'
       start = UtcIso (First-Dow $week2 ([DayOfWeek]::Wednesday)) 8 30
       end   = UtcIso (First-Dow $week2 ([DayOfWeek]::Wednesday)) 9 15
       location = 'Dr. Weber'; reminders = @(@{ minutesBefore = 60; channel = 'Email' }) }

    @{ title = '1:1 with Sam'; category = 'Work'; work = $true
       start = UtcIso (First-Dow $week2 ([DayOfWeek]::Thursday)) 10 0
       end   = UtcIso (First-Dow $week2 ([DayOfWeek]::Thursday)) 10 30 }

    @{ title = 'Movie night'; category = 'Social'
       start = UtcIso (First-Dow $week2 ([DayOfWeek]::Saturday)) 20 0
       end   = UtcIso (First-Dow $week2 ([DayOfWeek]::Saturday)) 22 30
       location = 'Cinestar' }

    @{ title = 'Lunch with Alex'; category = 'Social'; work = $true
       start = UtcIso (First-Dow $week3 ([DayOfWeek]::Friday)) 12 30
       end   = UtcIso (First-Dow $week3 ([DayOfWeek]::Friday)) 13 30
       location = 'Café Milano' }

    @{ title = 'Badminton with Chris'; category = 'Health'
       start = UtcIso (First-Dow $week3 ([DayOfWeek]::Saturday)) 10 0
       end   = UtcIso (First-Dow $week3 ([DayOfWeek]::Saturday)) 11 30 }

    @{ title = 'Car service'; category = 'Personal'
       start = UtcIso (First-Dow $week3 ([DayOfWeek]::Monday)) 7 45
       end   = UtcIso (First-Dow $week3 ([DayOfWeek]::Monday)) 8 15
       location = 'Autohaus Nord' }

    @{ title = 'Haircut'; category = 'Personal'
       start = UtcIso (First-Dow $week4 ([DayOfWeek]::Tuesday)) 16 30
       end   = UtcIso (First-Dow $week4 ([DayOfWeek]::Tuesday)) 17 0 }

    @{ title = 'Project deadline: v1.0'; category = 'Work'; work = $true
       start = UtcIso (First-Dow $week4 ([DayOfWeek]::Friday)) 17 0
       end   = UtcIso (First-Dow $week4 ([DayOfWeek]::Friday)) 18 0
       description = 'Ship it. 🚀'
       reminders = @(@{ minutesBefore = 120; channel = 'Email' }) }

    # --- one-off appointments, next month ---------------------------------------------
    @{ title = 'Quarterly planning'; category = 'Work'; work = $true
       start = UtcIso (First-Dow $nextMonth ([DayOfWeek]::Wednesday)) 9 0
       end   = UtcIso (First-Dow $nextMonth ([DayOfWeek]::Wednesday)) 12 0
       location = 'Room 1.01' }

    @{ title = 'Doctor check-up'; category = 'Health'
       start = UtcIso (First-Dow $nextMonth.AddDays(14) ([DayOfWeek]::Monday)) 9 30
       end   = UtcIso (First-Dow $nextMonth.AddDays(14) ([DayOfWeek]::Monday)) 10 0
       reminders = @(@{ minutesBefore = 60; channel = 'Email' }) }
)

# ---------- create --------------------------------------------------------------------

Write-Host "==> seeding $($events.Count) events (anchor: $($monthStart.ToString('yyyy-MM-dd')), tz: $timeZone)" -ForegroundColor Cyan

foreach ($e in $events) {
    $body = @{
        calendarId  = if ($e.work) { $workId } else { $personalId }
        title       = $e.title
        description = $e.description ?? $null
        location    = $e.location ?? $null
        categoryId  = $categoryId[$e.category]
        start       = $e.start
        end         = $e.end ?? $null
        allDay      = [bool]($e.allDay ?? $false)
        recurrence  = $e.recurrence ?? $null
        timeZone    = $timeZone
        reminders   = @($e.reminders ?? @())
    } | ConvertTo-Json -Depth 4

    $null = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/events" -Headers $auth `
        -ContentType 'application/json' -Body $body
    $kind = if ($e.recurrence) { 'recurring' } elseif ($e.allDay) { 'all-day' } else { 'one-off' }
    $cal  = if ($e.work) { 'Work' } else { 'Personal' }
    Write-Host ("    + {0,-24} ({1}, {2}, {3})" -f $e.title, $kind, $cal, $e.category)
}

Write-Host "==> done. Log in as $Email / $Password and enjoy the view." -ForegroundColor Green
