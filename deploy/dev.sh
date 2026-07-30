#!/usr/bin/env bash
# Builds and runs the CalendarIT backend + frontend together, for fast local testing.
#
#   Backend  -> http://localhost:5299   (dotnet run, ASPNETCORE_ENVIRONMENT=Development)
#   Frontend -> http://localhost:5173   (vite dev server, proxies /api to the backend)
#
# Both run in this one terminal with interleaved logs. Press Ctrl+C once to stop both.
#
# Usage:
#   ./deploy/dev.sh                 # restore + run backend and frontend
#   ./deploy/dev.sh --skip-install  # skip `npm install` (faster when deps are unchanged)
set -euo pipefail
# Monitor mode: each background job becomes its own process group, so the
# `kill -- -$pid` in cleanup() takes down dotnet/npm and all their children.
set -m

skip_install=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-install) skip_install=1; shift ;;
        -h|--help)
            grep '^#' "$0" | sed 's/^# \{0,1\}//' | grep -v '!/usr/bin/env'
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

# Repo root is the parent of this script's folder (deploy/).
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
backend_dir="$root/core/calendarITCore/calendarITCore"
frontend_dir="$root/web"

export ASPNETCORE_ENVIRONMENT=Development

backend_pid=""
frontend_pid=""

# Kill both process groups on exit (dotnet/npm spawn children; -PID targets the group).
cleanup() {
    trap - INT TERM EXIT
    [[ -n "$frontend_pid" ]] && kill -- -"$frontend_pid" 2>/dev/null || true
    [[ -n "$backend_pid"  ]] && kill -- -"$backend_pid"  2>/dev/null || true
    wait 2>/dev/null || true
}
trap cleanup INT TERM EXIT

# Frontend deps (only when missing, unless skipped).
if [[ "$skip_install" -eq 0 && ! -d "$frontend_dir/node_modules" ]]; then
    echo "==> npm install (frontend)"
    (cd "$frontend_dir" && npm install)
fi

# Build the backend once up front so compile errors surface before anything starts.
echo "==> dotnet build (backend)"
dotnet build "$backend_dir" -v minimal

echo "==> starting backend  -> http://localhost:5299"
( cd "$backend_dir" && exec dotnet run --no-build ) &
backend_pid=$!

echo "==> starting frontend -> http://localhost:5173"
( cd "$frontend_dir" && exec npm run dev ) &
frontend_pid=$!

echo "==> both running. Press Ctrl+C to stop."

# Exit as soon as either child stops, then cleanup() tears down the other.
wait -n
echo "==> a process exited; shutting down the other."
