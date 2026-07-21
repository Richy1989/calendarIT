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

# The API listens on :8080 (ASPNETCORE_HTTP_PORTS); nginx is the public face on :80.
dotnet /app/calendarITCore.dll &
api_pid=$!

nginx -g 'daemon off;' &
nginx_pid=$!

# Wait for whichever process exits first, then stop the other and exit with its code.
wait -n "$api_pid" "$nginx_pid"
exit_code=$?
kill "$api_pid" "$nginx_pid" 2>/dev/null || true
exit "$exit_code"
