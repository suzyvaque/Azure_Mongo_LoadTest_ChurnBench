<#
.SYNOPSIS
  Runs ONE generator host's share of a coordinated multi-host open-loop burst campaign
  (test_instruction.md §6.2). Executes locally on each load-gen VM.

.DESCRIPTION
  Each host runs the SAME config + run-tag, a distinct --host-id, the shared --host-count, and an
  identical --start-at UTC instant so every host's burst begins in the same wall-clock second and the
  realized ≥1,200 conn/s / ≥11,000 concurrent envelope is produced by all hosts combined (a single
  host cannot reach it without exhausting ephemeral ports / TLS CPU).

  The connection string is read from the target's machine env var already set on the host (never passed
  over the wire): documentdb->BMT_CONN, mongo-vm->BMT_CONN_MONGO, mongo-shard->BMT_CONN_MONGO_SHARD,
  cosmos-ru->BMT_CONN_COSMOS.

  After the run, results land in <RepoDir>\results\<tag>-<target>-...-hNNofMM-<stamp>\ . Pass -PushResults
  to git-commit+push them to the shared repo so a single operator box can `report merge` all hosts.

.PARAMETER Target      Backend key: documentdb | mongo-vm | mongo-shard | cosmos-ru.
.PARAMETER HostId      1-based id of THIS host within the campaign.
.PARAMETER HostCount   Total hosts in the campaign.
.PARAMETER RunTag      Shared campaign tag (identical on every host) used to group + merge artifacts.
.PARAMETER StartAtUtc  ISO-8601 UTC instant to begin the timed phase (identical on every host).
.PARAMETER Config      Config path. Default: config/production/full-workload-open-loop-multihost.json
.PARAMETER Scenario    Scenario. Default: burst.
.PARAMETER RepoDir     Repo root on this host. Default: C:\bmt
.PARAMETER PushResults If set, git add/commit/push results after the run (rebase-pull first).

.EXAMPLE
  # Host 2 of a 2-host DocumentDB burst, aligned to a shared start instant:
  .\Run-BurstHost.ps1 -Target documentdb -HostId 2 -HostCount 2 -RunTag docdb-m80-burst `
      -StartAtUtc 2026-07-16T06:00:00Z -PushResults
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('documentdb','mongo-vm','mongo-shard','cosmos-ru')]
    [string]$Target,
    [Parameter(Mandatory)] [int]$HostId,
    [Parameter(Mandatory)] [int]$HostCount,
    [Parameter(Mandatory)] [string]$RunTag,
    [Parameter(Mandatory)] [string]$StartAtUtc,
    [string]$Config = 'config/production/full-workload-open-loop-multihost.json',
    [ValidateSet('steady','burst','both')] [string]$Scenario = 'burst',
    [string]$RepoDir = 'C:\bmt',
    [switch]$PushResults,
    [switch]$NoPreflight
)

$ErrorActionPreference = 'Stop'

# The Azure run-command agent process can predate the machine PATH / DOTNET_ROOT updates written by
# host bootstrap (Setup-Gen2Host.ps1), so a bare `dotnet` is not resolvable when this script is invoked
# via `az vm run-command`. Refresh the process env from the registry so the SDK (C:\dotnet) is found.
$machinePath = [Environment]::GetEnvironmentVariable('PATH', 'Machine')
$userPath    = [Environment]::GetEnvironmentVariable('PATH', 'User')
$env:PATH = (@($machinePath, $userPath) | Where-Object { $_ }) -join ';'
$dotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Machine')
if ($dotnetRoot) { $env:DOTNET_ROOT = $dotnetRoot }

if ($HostId -lt 1 -or $HostId -gt $HostCount) {
    throw "HostId ($HostId) must be between 1 and HostCount ($HostCount)."
}

# Validate the shared start instant is a real, future-ish UTC time.
$startDt = [DateTimeOffset]::Parse($StartAtUtc, [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
$lead = ($startDt - [DateTimeOffset]::UtcNow).TotalSeconds
Write-Host "[host $HostId/$HostCount] target=$Target tag=$RunTag start=$($startDt.UtcDateTime.ToString('o')) (in $([math]::Round($lead,1))s)" -ForegroundColor Cyan
if ($lead -lt 0) {
    Write-Warning "start-at is in the past; the run will start immediately and may be misaligned with other hosts."
}

# Confirm the target's connection env var is present (do NOT print its value).
$envVar = switch ($Target) {
    'documentdb'  { 'BMT_CONN' }
    'mongo-vm'    { 'BMT_CONN_MONGO' }
    'mongo-shard' { 'BMT_CONN_MONGO_SHARD' }
    'cosmos-ru'   { 'BMT_CONN_COSMOS' }
}
if (-not [Environment]::GetEnvironmentVariable($envVar)) {
    # Fall back to machine scope (run-command runs as SYSTEM and may not inherit the user env).
    $machineVal = [Environment]::GetEnvironmentVariable($envVar, 'Machine')
    if ($machineVal) {
        Set-Item -Path "Env:$envVar" -Value $machineVal
    } else {
        throw "Connection env var '$envVar' for target '$Target' is not set (user or machine scope). Set it first (see runbook STEP 4)."
    }
}
Write-Host "[host $HostId/$HostCount] connection env '$envVar' present (value hidden)." -ForegroundColor DarkGray

Set-Location $RepoDir

# Build once (release) if the binary is stale; cheap no-op when already built.
Write-Host "[host $HostId/$HostCount] building (Release)..." -ForegroundColor DarkGray
dotnet build Bmt.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$preflightArg = @()
if ($NoPreflight) { $preflightArg = @('--no-preflight') }

Write-Host "[host $HostId/$HostCount] launching timed run..." -ForegroundColor Green
dotnet run --project src/Bmt.LoadGen -c Release --no-build -- `
    test `
    --target $Target `
    --scenario $Scenario `
    --config $Config `
    --host-id $HostId `
    --host-count $HostCount `
    --run-tag $RunTag `
    --start-at $StartAtUtc `
    --results results `
    @preflightArg
$runExit = $LASTEXITCODE
if ($runExit -ne 0) { throw "LoadGen run failed (exit $runExit)." }

Write-Host "[host $HostId/$HostCount] run complete." -ForegroundColor Green

if ($PushResults) {
    Write-Host "[host $HostId/$HostCount] pushing results to shared repo..." -ForegroundColor Cyan
    git config user.name  "host$HostId-$Target" | Out-Null
    git config user.email "host$HostId-$Target@benchmarks.local" | Out-Null
    git add results/
    git commit -m "results: $RunTag $Target host $HostId/$HostCount $(Get-Date -Format 'yyyy-MM-dd HH:mm')" | Out-Null
    # Never block on an interactive credential prompt. Under az vm run-command this script runs as
    # SYSTEM, which has no cached git credential, so GCM's `credential get` would hang forever. Force
    # non-interactive git (GIT_TERMINAL_PROMPT=0 + credential.interactive=false) so a missing credential
    # fails fast instead of stalling the whole run-command. Still works when a credential IS available.
    # Retry-pull-push in case peer hosts push concurrently. Force HTTP/1.1 on push: HTTP/2 POST stalls
    # indefinitely through the AZ1 NAT gateway (reads/fetch are fine on HTTP/2).
    $env:GIT_TERMINAL_PROMPT = '0'
    $pushed = $false
    for ($i = 0; $i -lt 5; $i++) {
        git -c http.version=HTTP/1.1 -c credential.interactive=false pull --rebase origin main
        git -c http.version=HTTP/1.1 -c credential.interactive=false -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=30 push origin main
        if ($LASTEXITCODE -eq 0) { $pushed = $true; break }
        Start-Sleep -Seconds (2 + (Get-Random -Maximum 4))
    }
    if ($pushed) {
        Write-Host "[host $HostId/$HostCount] results pushed." -ForegroundColor Green
    } else {
        $sha = (git rev-parse --short HEAD)
        Write-Host "[host $HostId/$HostCount] push skipped (no SYSTEM git credential); results committed locally at $sha for operator collection." -ForegroundColor Yellow
    }
}
