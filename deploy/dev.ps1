#requires -Version 7
# Builds and runs the CalendarIT backend + frontend together, for fast local testing.
#
#   Backend  -> http://localhost:5299   (dotnet run, ASPNETCORE_ENVIRONMENT=Development)
#   Frontend -> http://localhost:5173   (vite dev server, proxies /api to the backend)
#
# Both run in this one terminal with interleaved logs. Press Ctrl+C once to stop both.
#
# Usage:
#   ./deploy/dev.ps1                # restore + run backend and frontend
#   ./deploy/dev.ps1 -SkipInstall   # skip `npm install` (faster when deps are unchanged)

param(
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

# Repo root is the parent of this script's folder (deploy/).
$root       = Resolve-Path (Join-Path $PSScriptRoot '..')
$backendDir = Join-Path $root 'core/calendarITCore/calendarITCore'
$frontendDir = Join-Path $root 'web'

# Tree-kill a process by PID (dotnet/npm spawn children that Stop-Process alone leaves running).
function Stop-Tree($proc) {
    if ($proc -and -not $proc.HasExited) {
        taskkill /PID $proc.Id /T /F *> $null
    }
}

# Start a dev process with interleaved output. `-NoNewWindow` uses CreateProcess, which can't
# launch shims (npm ships as npm.cmd / npm.ps1) — only real .exe images. So launch a resolved
# .exe directly, and route anything else through `cmd.exe /c <name>`, letting cmd find the shim.
function Start-Dev($workDir, $command, [string[]]$arguments) {
    $exe = Get-Command $command -ErrorAction SilentlyContinue
    if ($exe -and $exe.CommandType -eq 'Application' -and $exe.Source -match '\.exe$') {
        return Start-Process -PassThru -NoNewWindow -WorkingDirectory $workDir `
            -FilePath $exe.Source -ArgumentList $arguments
    }
    return Start-Process -PassThru -NoNewWindow -WorkingDirectory $workDir `
        -FilePath $env:ComSpec -ArgumentList (@('/c', $command) + $arguments)
}

$procs = @()
try {
    # Frontend deps (only when missing, unless forced off).
    if (-not $SkipInstall -and -not (Test-Path (Join-Path $frontendDir 'node_modules'))) {
        Write-Host '==> npm install (frontend)' -ForegroundColor Cyan
        Push-Location $frontendDir
        try { npm install; if ($LASTEXITCODE -ne 0) { throw 'npm install failed' } }
        finally { Pop-Location }
    }

    # Build the backend once up front so compile errors surface before anything starts.
    Write-Host '==> dotnet build (backend)' -ForegroundColor Cyan
    dotnet build $backendDir -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

    Write-Host '==> starting backend  -> http://localhost:5299' -ForegroundColor Green
    $procs += Start-Dev $backendDir 'dotnet' @('run', '--no-build')

    Write-Host '==> starting frontend -> http://localhost:5173' -ForegroundColor Green
    $procs += Start-Dev $frontendDir 'npm' @('run', 'dev')

    Write-Host '==> both running. Press Ctrl+C to stop.' -ForegroundColor Cyan

    # Wait until either process exits (or Ctrl+C drops us into finally).
    while ($true) {
        if ($procs | Where-Object { $_.HasExited }) {
            Write-Host '==> a process exited; shutting down the other.' -ForegroundColor Yellow
            break
        }
        Start-Sleep -Milliseconds 400
    }
}
finally {
    foreach ($p in $procs) { Stop-Tree $p }
}
