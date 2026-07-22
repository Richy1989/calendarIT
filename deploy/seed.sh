#!/usr/bin/env bash
# Seeds the CalendarIT backend with a demo user and two calendars full of demo events.
#
#   User      -> test@test.com / Test1234#1234 (registered if missing, otherwise logged in)
#   Calendars -> "Personal" (the default) and "Work" (created if missing), so the
#                calendar switcher has something to toggle.
#   Events    -> anchored to the FIRST DAY OF THE CURRENT MONTH, so the seeded calendar
#                always looks current: weekly recurring series (standup, gym, team sync,
#                sprint review, family dinner), all-day + multi-day events (birthday,
#                conference, payday), and one-off appointments across this and next month.
#                Work-ish events land in "Work", everything else in "Personal".
#
# The backend must already be running (e.g. via ./deploy/dev.sh).
# Requires: curl, jq, GNU date.
#
# Usage:
#   ./deploy/seed.sh                          # seed against http://localhost:5299
#   ./deploy/seed.sh --base-url http://host   # seed a different instance
#   ./deploy/seed.sh --force                  # seed even if the user already has events
set -euo pipefail

base_url='http://localhost:5299'
email='test@test.com'
password='Test1234#1234'
force=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --base-url) base_url="$2"; shift 2 ;;
        --email)    email="$2";    shift 2 ;;
        --password) password="$2"; shift 2 ;;
        --force)    force=1;       shift ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

command -v jq >/dev/null || { echo 'seed.sh requires jq' >&2; exit 1; }

# ---------- auth ----------------------------------------------------------------------

credentials=$(jq -n --arg e "$email" --arg p "$password" '{email:$e,password:$p}')

register=$(curl -sS -w '\n%{http_code}' -X POST "$base_url/api/auth/register" \
    -H 'Content-Type: application/json' -d "$credentials") \
    || { echo "Cannot reach the backend at $base_url. Start it first (./deploy/dev.sh)." >&2; exit 1; }
status=$(tail -n1 <<<"$register")
tokens=$(sed '$d' <<<"$register")

if [[ "$status" == 200 ]]; then
    echo "==> registered $email"
else
    # Most likely the user already exists — fall back to login.
    login=$(curl -sS -w '\n%{http_code}' -X POST "$base_url/api/auth/login" \
        -H 'Content-Type: application/json' -d "$credentials")
    status=$(tail -n1 <<<"$login")
    tokens=$(sed '$d' <<<"$login")
    [[ "$status" == 200 ]] || { echo "Register failed and login failed too (login=$status)." >&2; exit 1; }
    echo "==> $email already exists, logged in"
fi

access_token=$(jq -r '.accessToken' <<<"$tokens")
auth="Authorization: Bearer $access_token"

# ---------- calendars -----------------------------------------------------------------
# Listing bootstraps the default "Personal" calendar server-side; "Work" is ours to add.

calendars=$(curl -sS -f "$base_url/api/calendars" -H "$auth")
personal_id=$(jq -r '.[0].id' <<<"$calendars")
work_id=$(jq -r '[.[] | select(.name == "Work")][0].id // empty' <<<"$calendars")
if [[ -z "$work_id" ]]; then
    work_id=$(jq -n '{name:"Work"}' | curl -sS -f -X POST "$base_url/api/calendars" \
        -H "$auth" -H 'Content-Type: application/json' -d @- | jq -r '.id')
    echo "==> created 'Work' calendar"
fi

# ---------- date helpers --------------------------------------------------------------

# The IANA time zone the events are authored in (backend expands recurrences DST-correct).
time_zone=$(timedatectl show -p Timezone --value 2>/dev/null \
    || readlink /etc/localtime 2>/dev/null | sed 's|.*/zoneinfo/||' \
    || echo UTC)
[[ -n "$time_zone" ]] || time_zone=UTC

month_start=$(date +%Y-%m-01)

# First occurrence of an ISO weekday (1=Mon..7=Sun) on or after $1 (YYYY-MM-DD).
first_dow() {
    local d=$1
    while [[ $(date -d "$d" +%u) != "$2" ]]; do d=$(date -d "$d + 1 day" +%F); done
    echo "$d"
}

# Local wall-clock time on a day -> UTC ISO instant (per-date DST handled by date -u).
utc_iso()    { date -u -d "$1 $2" +%Y-%m-%dT%H:%M:%SZ; }
# All-day events use date-at-UTC-midnight; end is the INCLUSIVE last day (API convention).
allday_iso() { echo "${1}T00:00:00Z"; }
plus_days()  { date -d "$1 + $2 days" +%F; }

# ---------- idempotency guard ---------------------------------------------------------

existing=$(curl -sS -G "$base_url/api/events" -H "$auth" \
    --data-urlencode "from=$(allday_iso "$month_start")" \
    --data-urlencode "to=$(allday_iso "$(date -d "$month_start + 3 months" +%F)")" \
    | jq 'length')
if [[ "$existing" -gt 0 && "$force" -ne 1 ]]; then
    echo "==> $email already has $existing event(s) from ${month_start%-*} onward — nothing seeded."
    echo "    Re-run with --force to seed anyway (will create duplicates)."
    exit 0
fi

# ---------- create --------------------------------------------------------------------
# Colors are exact CSS3-named values (app swatch convention, lossless for iCal COLOR).

created=0
# The calendar the next post_event calls target — flip between the two before each group.
cal_id=''
cal_name=''
use_personal() { cal_id=$personal_id; cal_name='Personal'; }
use_work()     { cal_id=$work_id;     cal_name='Work'; }

# post_event TITLE COLOR START END ALLDAY RECURRENCE LOCATION DESCRIPTION REMINDERS_JSON
post_event() {
    jq -n --arg title "$1" --arg color "$2" --arg start "$3" --arg end "$4" \
          --argjson allDay "$5" --arg rec "$6" --arg loc "$7" --arg desc "$8" \
          --argjson reminders "${9:-[]}" --arg tz "$time_zone" --arg cal "$cal_id" '{
        calendarId: $cal,
        title: $title, color: $color, start: $start,
        end: (if $end == "" then null else $end end),
        allDay: $allDay,
        recurrence: (if $rec == "" then null else $rec end),
        location: (if $loc == "" then null else $loc end),
        description: (if $desc == "" then null else $desc end),
        timeZone: $tz, reminders: $reminders
    }' | curl -sS -f -X POST "$base_url/api/events" -H "$auth" \
        -H 'Content-Type: application/json' -d @- >/dev/null
    echo "    + $1 ($cal_name)"
    created=$((created + 1))
}

monday=$(first_dow "$month_start" 1)
tuesday=$(first_dow "$month_start" 2)
friday=$(first_dow "$month_start" 5)
sunday=$(first_dow "$month_start" 7)
week2=$(plus_days "$month_start" 7)
week3=$(plus_days "$month_start" 14)
week4=$(plus_days "$month_start" 21)
next_month=$(date -d "$month_start + 1 month" +%F)
conf_start=$(first_dow "$week3" 2)

echo "==> seeding events (anchor: $month_start, tz: $time_zone)"

# --- recurring series ---
use_work
post_event 'Team standup' '#4682B4' "$(utc_iso "$monday" 09:15)" "$(utc_iso "$monday" 09:30)" false \
    'FREQ=WEEKLY;BYDAY=MO,WE,FR' 'Zoom' "Daily sync — what shipped, what's next, blockers." \
    '[{"minutesBefore":10,"channel":"Email"}]'
post_event 'Team sync' '#7B68EE' "$(utc_iso "$monday" 11:00)" "$(utc_iso "$monday" 12:00)" false \
    'FREQ=WEEKLY' 'Room 2.04' 'Weekly planning and demos.'
post_event 'Sprint review' '#FFA500' "$(utc_iso "$friday" 14:00)" "$(utc_iso "$friday" 15:00)" false \
    'FREQ=WEEKLY;INTERVAL=2' 'Room 1.01' 'Demo, retro, next sprint scope.'
use_personal
post_event 'Gym' '#FF6347' "$(utc_iso "$tuesday" 18:00)" "$(utc_iso "$tuesday" 19:30)" false \
    'FREQ=WEEKLY;BYDAY=TU,TH' 'FitOne' ''
post_event 'Family dinner' '#3CB371' "$(utc_iso "$sunday" 18:00)" "$(utc_iso "$sunday" 20:00)" false \
    'FREQ=WEEKLY' '' ''

# --- all-day / multi-day ---
use_work
post_event 'DevConf' '#40E0D0' "$(allday_iso "$conf_start")" "$(allday_iso "$(plus_days "$conf_start" 2)")" true \
    '' 'Convention Center' 'Three days of talks and workshops.'
use_personal
post_event "Mom's birthday" '#FF69B4' "$(allday_iso "$(plus_days "$month_start" 11)")" '' true '' '' '' \
    '[{"minutesBefore":1440,"channel":"Email"}]'
post_event 'Payday' '#FFD700' "$(allday_iso "$(plus_days "$month_start" 27)")" '' true '' '' ''
post_event 'Day off' '#3CB371' "$(allday_iso "$(first_dow "$(plus_days "$next_month" 7)" 5)")" '' true '' '' ''

# --- one-off appointments, this month ---
wed2=$(first_dow "$week2" 3); thu2=$(first_dow "$week2" 4); sat2=$(first_dow "$week2" 6)
fri3=$(first_dow "$week3" 5); sat3=$(first_dow "$week3" 6); mon3=$(first_dow "$week3" 1)
tue4=$(first_dow "$week4" 2); fri4=$(first_dow "$week4" 5)
use_work
post_event '1:1 with Sam' '#7B68EE' "$(utc_iso "$thu2" 10:00)" "$(utc_iso "$thu2" 10:30)" false '' '' ''
post_event 'Lunch with Alex' '#FFA500' "$(utc_iso "$fri3" 12:30)" "$(utc_iso "$fri3" 13:30)" false '' 'Café Milano' ''
post_event 'Project deadline: v1.0' '#DC143C' "$(utc_iso "$fri4" 17:00)" "$(utc_iso "$fri4" 18:00)" false '' '' 'Ship it. 🚀' \
    '[{"minutesBefore":120,"channel":"Email"}]'
use_personal
post_event 'Dentist' '#6495ED' "$(utc_iso "$wed2" 08:30)" "$(utc_iso "$wed2" 09:15)" false '' 'Dr. Weber' '' \
    '[{"minutesBefore":60,"channel":"Email"}]'
post_event 'Movie night' '#BA55D3' "$(utc_iso "$sat2" 20:00)" "$(utc_iso "$sat2" 22:30)" false '' 'Cinestar' ''
post_event 'Badminton with Chris' '#FF6347' "$(utc_iso "$sat3" 10:00)" "$(utc_iso "$sat3" 11:30)" false '' '' ''
post_event 'Car service' '#4682B4' "$(utc_iso "$mon3" 07:45)" "$(utc_iso "$mon3" 08:15)" false '' 'Autohaus Nord' ''
post_event 'Haircut' '#BA55D3' "$(utc_iso "$tue4" 16:30)" "$(utc_iso "$tue4" 17:00)" false '' '' ''

# --- one-off appointments, next month ---
wed_n=$(first_dow "$next_month" 3)
mon_n=$(first_dow "$(plus_days "$next_month" 14)" 1)
use_work
post_event 'Quarterly planning' '#7B68EE' "$(utc_iso "$wed_n" 09:00)" "$(utc_iso "$wed_n" 12:00)" false '' 'Room 1.01' ''
use_personal
post_event 'Doctor check-up' '#6495ED' "$(utc_iso "$mon_n" 09:30)" "$(utc_iso "$mon_n" 10:00)" false '' '' '' \
    '[{"minutesBefore":60,"channel":"Email"}]'

echo "==> done ($created events). Log in as $email / $password and enjoy the view."
