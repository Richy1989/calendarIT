#!/usr/bin/env bash
# Builds the single-container CalendarIT image for Unraid (linux/amd64) and pushes it to Docker Hub.
#
# Prerequisites (one-time):
#   docker login                 # log in as richy1989
#   docker buildx version        # buildx ships with Docker Desktop / modern Docker Engine
#
# Usage:
#   ./deploy/build-and-push.sh                 # builds & pushes richy1989/calendarit:dev
#   ./deploy/build-and-push.sh --tag v0.1.0    # release: also pushes :latest and :v0.1.0
#
# :dev always tracks the newest build. :latest only moves on a release, i.e. when a real
# version tag is passed — so a dev push can never clobber production.
set -euo pipefail

IMAGE="richy1989/calendarit"
TAG=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --image) IMAGE="$2"; shift 2 ;;
        --tag)   TAG="$2";   shift 2 ;;
        -h|--help)
            grep '^#' "$0" | sed 's/^# \{0,1\}//' | grep -v '!/usr/bin/env'
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

# Build context must be the repo root (the parent of this script's folder).
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

# :dev is always pushed; a version tag additionally moves :latest and pushes the version.
tags=(-t "${IMAGE}:dev")
build_args=()
if [[ -n "$TAG" && "$TAG" != "dev" ]]; then
    # Only accept real version tags here. Stamping a non-version (like "latest" or "dev")
    # would break the build: Docker exposes the ARG as an env var and MSBuild reads it as
    # the project's $(Version), which NuGet then fails to parse during restore.
    if [[ ! "$TAG" =~ ^v?[0-9]+(\.[0-9]+){2}(-[0-9A-Za-z.-]+)?$ ]]; then
        echo "Tag '$TAG' is not a version tag (expected e.g. v0.1.0). Omit --tag for a dev-only push." >&2
        exit 1
    fi
    tags+=(-t "${IMAGE}:${TAG}" -t "${IMAGE}:latest")
    build_args+=(--build-arg VERSION="${TAG#v}")
fi

pushed="${tags[*]}"; pushed="${pushed//-t /}"
echo "Building and pushing ${pushed} for linux/amd64 (Unraid)..."
docker buildx build --platform linux/amd64 -f deploy/Dockerfile "${build_args[@]}" "${tags[@]}" --push .

echo "Done. On Unraid, pull/refresh: ${IMAGE}:${TAG:-dev}"
