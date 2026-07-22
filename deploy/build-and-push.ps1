#requires -Version 5
# Builds the single-container CalendarIT image for Unraid (linux/amd64) and pushes it to Docker Hub.
#
# Prerequisites (one-time):
#   docker login                 # log in as richy1989
#   docker buildx version        # buildx ships with Docker Desktop
#
# Usage:
#   ./deploy/build-and-push.ps1                  # builds & pushes richy1989/calendarit:dev
#   ./deploy/build-and-push.ps1 -Tag v0.1.0      # release: also pushes :latest and :v0.1.0
#
# :dev always tracks the newest build. :latest only moves on a release, i.e. when a real
# version tag is passed — so a dev push can never clobber production.

param(
    [string]$Image = 'richy1989/calendarit',
    [string]$Tag   = ''
)

$ErrorActionPreference = 'Stop'

# Repo root is the parent of this script's folder; build context must be the repo root.
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    # :dev is always pushed; a version tag additionally moves :latest and pushes the version.
    $tags = @("${Image}:dev")
    $buildArgs = @()
    if ($Tag -and $Tag -ne 'dev') {
        # Only accept real version tags here. Stamping a non-version (like "latest" or "dev")
        # would break the build: Docker exposes the ARG as an env var and MSBuild reads it as
        # the project's $(Version), which NuGet then fails to parse during restore.
        if ($Tag -notmatch '^v?\d+(\.\d+){2}(-[0-9A-Za-z.\-]+)?$') {
            throw "Tag '$Tag' is not a version tag (expected e.g. v0.1.0). Omit -Tag for a dev-only push."
        }
        $tags += "${Image}:$Tag"
        $tags += "${Image}:latest"
        $buildArgs = @('--build-arg', "VERSION=$($Tag.TrimStart('v'))")
    }
    $tagArgs = $tags | ForEach-Object { @('-t', $_) } | ForEach-Object { $_ }

    Write-Host "Building and pushing $($tags -join ', ') for linux/amd64 (Unraid)..." -ForegroundColor Cyan
    docker buildx build --platform linux/amd64 -f deploy/Dockerfile @buildArgs @tagArgs --push .
    if ($LASTEXITCODE -ne 0) { throw "docker buildx build failed with exit code $LASTEXITCODE" }

    Write-Host "Done. On Unraid, pull/refresh: ${Image}:$(if ($Tag) { $Tag } else { 'dev' })" -ForegroundColor Green
}
finally {
    Pop-Location
}
