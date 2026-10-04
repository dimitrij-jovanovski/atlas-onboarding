# Builds and starts all Atlas services on the host. Ctrl+C stops them all.
#
#   ./scripts/run.ps1            SQL Server + Seq from /platform (docker compose up -d first)
#   ./scripts/run.ps1 -Sqlite    no Docker needed: one SQLite file per market under .data/
param([switch]$Sqlite)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path

if ($Sqlite) {
    $env:Database__Provider = 'Sqlite'
    $env:Database__ConnectionStringTemplate = "Data Source=$root/.data/atlas_{market}.db"
    Write-Host "Using SQLite in $root/.data"
}

dotnet build "$root/Atlas.sln" -v q -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

function Start-AtlasService([string]$name) {
    Start-Process dotnet -ArgumentList @('run', '--no-build', '--project', "$root/src/$name") -NoNewWindow -PassThru
}

$processes = @()
try {
    $processes += Start-AtlasService 'Atlas.FakeProviders'
    $processes += Start-AtlasService 'Atlas.Onboarding.Api'   # owns the schema: creates the per-market databases

    Write-Host 'Waiting for the onboarding API...'
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        try { Invoke-RestMethod http://localhost:5100/health | Out-Null; break } catch { Start-Sleep -Seconds 1 }
    }

    $processes += Start-AtlasService 'Atlas.Verification.Worker'
    $processes += Start-AtlasService 'Atlas.Backoffice.Api'

    Write-Host @"

  Onboarding API (mobile)   http://localhost:5100
  Back office (staff)       http://localhost:5200
  Fake IDNow / World-Check  http://localhost:5300
  Seq (logs)                http://localhost:5341   (only with docker compose)

  Try:  ./scripts/demo.ps1      Ctrl+C to stop everything.

"@
    Wait-Process -Id $processes.Id
}
finally {
    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
}
