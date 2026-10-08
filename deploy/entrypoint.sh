#!/usr/bin/env bash
# Starts the two processes that make up the single-container deployment: the .NET API and nginx.
# If either one exits, we tear the whole container down so Docker/Unraid can restart it cleanly
# (rather than limping along with half the app dead).
set -euo pipefail

mkdir -p /data

# The app requires a JWT signing key (>=32 chars). If none is supplied, generate one and persist
# it under /data so the container works out-of-the-box AND logins survive restarts. Set
# JWT_SIGNING_KEY yourself to override (e.g. to share one key across multiple instances).
if [ -z "${JWT_SIGNING_KEY:-}" ]; then
    keyfile=/data/jwt-signing.key
    if [ ! -s "$keyfile" ]; then
        head -c 48 /dev/urandom | base64 | tr -d '\n' > "$keyfile"
    fi
    JWT_SIGNING_KEY="$(cat "$keyfile")"
    export JWT_SIGNING_KEY
fi

# Everything under /data belongs to the user the API runs as (PUID/PGID, default 1654 — the base
# image's "app" user). Older images ran as root, so an existing volume is taken over here.
uid="${PUID:-${APP_UID:-1654}}"
gid="${PGID:-${APP_UID:-1654}}"
chown -R "$uid:$gid" /data

# The API listens on :8080 (ASPNETCORE_HTTP_PORTS) as that unprivileged user; nginx — the public
# face on :80 — keeps its usual root master with unprivileged workers.
setpriv --reuid="$uid" --regid="$gid" --clear-groups -- dotnet /app/calendarITCore.dll &
api_pid=$!

# nginx's access log in the API's colours, or plain when NO_COLOR is set (https://no-color.org) —
# the API's formatter makes the same choice. nginx can't read the environment, so the choice is
# written where nginx.conf includes it, fresh at every start.
if [ -n "${NO_COLOR:-}" ]; then log_suffix=_plain; else log_suffix=; fi
printf 'access_log /dev/stdout client_error%s if=$log_client_error;\naccess_log /dev/stdout server_error%s if=$log_server_error;\n' \
    "$log_suffix" "$log_suffix" > /etc/nginx/calendarit-access-log.conf

nginx -g 'daemon off;' &
nginx_pid=$!

# As PID 1 this script gets `docker stop`'s SIGTERM, and a signal with no handler is ignored by
# PID 1: Docker would wait out its timeout and SIGKILL both, cutting the API off mid-request (and
# before it could log that it was shutting down). Pass it on, so the API finishes what it's doing
# and nginx closes its connections.
trap 'kill -TERM "$api_pid" "$nginx_pid" 2>/dev/null || true' TERM INT

# Wait for whichever process exits first (or for the stop signal, which ends the wait early), then
# stop the other and exit with its code. Both have to be waited for before this script exits: when
# PID 1 exits, the kernel kills everything left in the container on the spot, which would cut the
# API off in the middle of the shutdown the trap had just asked it for.
# The `||` matters as much: a wait the signal ends returns 143, and under `set -e` that alone
# would exit the script before the lines below ran.
exit_code=0
wait -n "$api_pid" "$nginx_pid" || exit_code=$?
kill "$api_pid" "$nginx_pid" 2>/dev/null || true
wait "$api_pid" "$nginx_pid" 2>/dev/null || true
exit "$exit_code"
