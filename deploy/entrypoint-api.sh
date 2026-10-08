#!/usr/bin/env bash
# Starts the .NET app as an unprivileged user (the "runtime" image: Kestrel serves API + SPA).
#
# The container starts as root only to make the data directory writable by that user — volumes
# created by older images, which ran everything as root, are taken over here — and then drops to
# PUID/PGID (default: the base image's "app" user, 1654) for good. If the container was already
# started as a non-root user (compose `user:`), there is nothing to drop and the app just runs.
set -euo pipefail

# The app resolves a relative APPDATA_PATH against its working directory (/app); unset means "appdata".
data="${APPDATA_PATH:-appdata}"
case "$data" in /*) ;; *) data="/app/$data" ;; esac
uid="${PUID:-${APP_UID:-1654}}"
gid="${PGID:-${APP_UID:-1654}}"

if [ "$(id -u)" = "0" ]; then
    mkdir -p "$data"
    chown -R "$uid:$gid" "$data"
    exec setpriv --reuid="$uid" --regid="$gid" --clear-groups -- dotnet /app/calendarITCore.dll "$@"
fi

exec dotnet /app/calendarITCore.dll "$@"
