#!/usr/bin/env bash
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
#   Events     -> from seed-events.json (shared with seed.ps1), anchored to the FIRST DAY
#                 OF THE CURRENT MONTH, so the seeded calendar
#                 always looks current: weekly recurring series (standup, gym, team sync,
#                 sprint review, family dinner), all-day + multi-day events (birthday,
#                 conference, payday), and one-off appointments across this and next month.
#                 Work-ish events land in "Work", everything else in "Personal".
#
#   Tasks      -> due dates relative to today, priorities, undated tasks and subtasks.
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
personal_id=$(jq -er '([.[] | select(.name == "Personal" and .subscription == null)][0] // [.[] | select(.name != "Work" and .subscription == null)][0]).id' <<<"$calendars")
work_id=$(jq -r '[.[] | select(.name == "Work")][0].id // empty' <<<"$calendars")
if [[ -z "$work_id" ]]; then
    work_id=$(jq -n '{name:"Work"}' | curl -sS -f -X POST "$base_url/api/calendars" \
        -H "$auth" -H 'Content-Type: application/json' -d @- | jq -r '.id')
    echo "==> created 'Work' calendar"
fi

# ---------- categories ----------------------------------------------------------------
# New accounts start with default categories (Work, Personal, Family, Important); we reuse
# any that already exist and create the rest. Colours are exact CSS3-named values (app
# swatch convention, lossless for the iCalendar COLOR property). Each event takes its
# colour from its category.

declare -A category_color=(
    [Work]='#6495ED'      # cornflowerblue
    [Personal]='#3CB371'  # mediumseagreen
    [Family]='#DAA520'    # goldenrod
    [Health]='#FF6347'    # tomato
    [Social]='#BA55D3'    # mediumorchid
    [Finance]='#FFD700'   # gold
    [Travel]='#40E0D0'    # turquoise
)

declare -A category_id
existing_categories=$(curl -sS -f "$base_url/api/categories" -H "$auth")
while IFS=$'\t' read -r cname cid; do
    [[ -n "$cname" ]] && category_id["$cname"]=$cid
done < <(jq -r '.[] | [.name, .id] | @tsv' <<<"$existing_categories")

for name in "${!category_color[@]}"; do
    if [[ -z "${category_id[$name]:-}" ]]; then
        category_id["$name"]=$(jq -n --arg n "$name" --arg c "${category_color[$name]}" '{name:$n,color:$c}' \
            | curl -sS -f -X POST "$base_url/api/categories" \
                -H "$auth" -H 'Content-Type: application/json' -d @- | jq -r '.id')
        echo "==> created category '$name'"
    fi
done

# ---------- date helpers --------------------------------------------------------------

# The IANA time zone the events are authored in (backend expands recurrences DST-correct).
time_zone=${TZ:-$(timedatectl show -p Timezone --value 2>/dev/null \
    || readlink /etc/localtime 2>/dev/null | sed 's|.*/zoneinfo/||' \
    || echo UTC)}
[[ -n "$time_zone" ]] || time_zone=UTC

month_start=$(date +%Y-%m-01)

# First occurrence of an ISO weekday (1=Mon..7=Sun) on or after $1 (YYYY-MM-DD).
first_dow() {
    local d=$1
    while [[ $(date -d "$d" +%u) != "$2" ]]; do d=$(date -d "$d + 1 day" +%F); done
    echo "$d"
}

# Local wall-clock time on a day -> UTC ISO instant (convert the local instant before formatting in UTC).
utc_iso()    { date -u -d "@$(date -d "$1 $2" +%s)" +%Y-%m-%dT%H:%M:%SZ; }
# All-day events use date-at-UTC-midnight; end is the INCLUSIVE last day (API convention).
allday_iso() { echo "${1}T00:00:00Z"; }
plus_days()  { date -d "$1 + $2 days" +%F; }

# ---------- tasks ----------------------------------------------------------------------
# Add missing demo tasks even when upgrading an account previously seeded with events.
# Match the calendar and parent as well as the title, including completed tasks.
task_fixture="$(dirname -- "${BASH_SOURCE[0]}")/seed-tasks.json"
tasks=$(curl -fsS "$base_url/api/tasks?includeDone=true" -H "$auth")
today=$(date +%F)
while IFS= read -r spec; do
    calendar=$(jq -r '.calendar' <<<"$spec")
    task_cal=$personal_id
    [[ "$calendar" != Work ]] || task_cal=$work_id
    task_body=$(jq -nc --argjson spec "$spec" --argjson tasks "$tasks" --arg cal "$task_cal" '
        ($tasks | map(select(.calendarId == $cal))) as $inCalendar |
        (if $spec.parent then
            ($inCalendar | map(select(.title == $spec.parent and .parentId == null)) | first.id // error("Missing parent task"))
         else null end) as $parent |
        if any($inCalendar[]; .title == $spec.title and .parentId == $parent) then empty
        else {calendarId:$cal, title:$spec.title, priority:$spec.priority, parentId:$parent} end')
    [[ -n "$task_body" ]] || continue
    days=$(jq -r '.days // empty' <<<"$spec")
    if [[ -n "$days" ]]; then
        due=$(date -d "$today $days days" +%F)
        task_body=$(jq --arg due "$(allday_iso "$due")" '. + {due:$due, dueAllDay:true}' <<<"$task_body")
    fi
    added=$(curl -fsS "$base_url/api/tasks" -H "$auth" -H 'Content-Type: application/json' -d "$task_body")
    tasks=$(jq --argjson task "$added" '. + [$task]' <<<"$tasks")
    echo "    + task: $(jq -r '.title' <<<"$added")"
done < <(jq -c '.[]' "$task_fixture")

# ---------- idempotency guard ---------------------------------------------------------

existing=$(curl -sS -G "$base_url/api/events" -H "$auth" \
    --data-urlencode "from=$(allday_iso "$month_start")" \
    --data-urlencode "to=$(allday_iso "$(date -d "$month_start + 3 months" +%F)")" \
    | jq 'length')
if [[ "$existing" -gt 0 && "$force" -ne 1 ]]; then
    echo "==> $email already has $existing event(s) from ${month_start%-*} onward — skipping event creation."
    echo "    Re-run with --force to seed anyway (will create duplicates)."
    exit 0
fi

# ---------- create --------------------------------------------------------------------
# The events live in seed-events.json, shared with seed.ps1. Each names its calendar and
# category (its colour comes from the category) and a day relative to the first of this or
# next month: "plusDays" on from there, then the first "weekday" (1=Mon..7=Sun) on or after.
# Timed events give local wall-clock "start"/"end" times; all-day ones span "days" days.

event_fixture="$(dirname -- "${BASH_SOURCE[0]}")/seed-events.json"
next_month=$(date -d "$month_start + 1 month" +%F)

# fixture_day FROM PLUS_DAYS WEEKDAY -> YYYY-MM-DD
fixture_day() {
    local d=$month_start
    [[ "$1" != nextMonth ]] || d=$next_month
    d=$(plus_days "$d" "$2")
    [[ -z "$3" ]] || d=$(first_dow "$d" "$3")
    echo "$d"
}

echo "==> seeding events (anchor: $month_start, tz: $time_zone)"
created=0
while IFS= read -r spec; do
    # Unit-separated, not tab-separated: read would merge empty tab fields.
    IFS=$'\x1f' read -r title calendar category from plus weekday all_day days start_time end_time < <(jq -r '[
        .title, .calendar, .category, .on.from, (.on.plusDays // 0), (.on.weekday // ""),
        (.allDay // false), (.days // 1), (.start // ""), (.end // "")] | map(tostring) | join("\u001f")' <<<"$spec")
    day=$(fixture_day "$from" "$plus" "$weekday")
    if [[ "$all_day" == true ]]; then
        # All-day: the date at UTC midnight; the end is the INCLUSIVE last day (API convention).
        start=$(allday_iso "$day")
        end=''
        [[ "$days" -le 1 ]] || end=$(allday_iso "$(plus_days "$day" $((days - 1)))")
    else
        start=$(utc_iso "$day" "$start_time")
        end=$(utc_iso "$day" "$end_time")
    fi
    cal=$personal_id
    [[ "$calendar" != Work ]] || cal=$work_id
    jq -c --arg cal "$cal" --arg category "${category_id[$category]:-}" --arg start "$start" --arg end "$end" \
          --arg tz "$time_zone" '{
        calendarId: $cal,
        title: .title,
        categoryId: (if $category == "" then null else $category end),
        start: $start,
        end: (if $end == "" then null else $end end),
        allDay: (.allDay // false),
        recurrence: .recurrence,
        location: .location,
        description: .description,
        timeZone: $tz,
        reminders: (.reminders // [])
    }' <<<"$spec" | curl -sS -f -X POST "$base_url/api/events" -H "$auth" \
        -H 'Content-Type: application/json' -d @- >/dev/null
    echo "    + $title ($calendar / $category)"
    created=$((created + 1))
done < <(jq -c '.[]' "$event_fixture")

echo "==> done ($created events). Demo calendar ready for $email."
