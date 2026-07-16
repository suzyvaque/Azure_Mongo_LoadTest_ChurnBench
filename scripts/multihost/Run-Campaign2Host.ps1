<#
.SYNOPSIS
  Run ONE host's share of the CANONICAL 2-host open-loop burst campaign. Every load-shaping constant is
  PINNED here so the run is byte-identical across targets — the DocumentDB campaign on the az2 pair and
  the mongo-shard campaign on the az3 pair offer the SAME connection load; only --target, --host-id and
  the shared --start-at differ. Self-contained: auto-detects the repo root and dotnet, injects the
  target's Machine-scope connection env var, then launches the timed run.

  WHY THE LOAD IS IDENTICAL ACROSS TARGETS (do not "tune" per target — that breaks comparability):
    * PINNED_CONFIG fixes lambda, Min/MaxTasksPerJob, DurationSeconds, Iterations and TaskSleepMs.
    * PINNED_HOSTCOUNT = 2 and the per-host RNG seed = DatasetSeed(42) + HostId are TARGET-INDEPENDENT,
      so the Poisson arrival schedule, the tasks-per-Job draws and the ReqId access pattern are the same
      sequence of events for documentdb and mongo-shard.
    * Open-loop (gated=false) means realized load == offered schedule regardless of backend speed, so a
      slower backend does NOT reduce the offered connections — the INPUT is held constant.
  The ONLY legitimate per-run variation is --start-at (when the burst begins) — it changes timing, not
  the load shape.

  SIZING (see config/production/full-workload-open-loop-2host.json):
    lambda=5/host x mean(150..500=325) ~= 1,625 conn/s/host (< ~1,850 ephemeral ceiling) ->
    2 hosts ~= 3,250 conn/s combined; hold = TaskSleepMs 3.5 s + ~0.1 s op ~= 3.6 s ->
    concurrency ~= 3,250 x 3.6 ~= 11,700 (>= 11,000 target; >= 1,210 conn/s target).

  AZ RULE (fairness): drive each backend ONLY from its co-located pair.
    * mongo-shard  <- az3 pair: vm-dbtest-hpc-0-gen2 (host-id 1), vm-dbtest-hpc-0 (host-id 2)
    * documentdb   <- az2 pair: vm-dbtest-hpc-0-az2-gen2 (host-id 1), vm-dbtest-hpc-0-az2 (host-id 2)

.PARAMETER Target      documentdb | mongo-shard  (resolves conn from BMT_CONN / BMT_CONN_MONGO_SHARD).
.PARAMETER HostId      1 (the gen2 sibling) or 2 (the gen1 sibling) within the co-located pair.
.PARAMETER StartAtUtc  ISO-8601 UTC instant shared by BOTH hosts of the pair (e.g. 2026-07-16T06:00:00Z).
.PARAMETER NoPreflight Skip the in-run preflight gate (NOT recommended; preflight already passed).

.EXAMPLE
  # az3 mongo-shard, both hosts aligned to the same UTC second:
  #   host 1 (gen2):  .\Run-Campaign2Host.ps1 -Target mongo-shard -HostId 1 -StartAtUtc 2026-07-16T06:00:00Z
  #   host 2 (gen1):  .\Run-Campaign2Host.ps1 -Target mongo-shard -HostId 2 -StartAtUtc 2026-07-16T06:00:00Z
.EXAMPLE
  # az2 documentdb, later, EXACT same command shape (only Target changes):
  #   host 1 (gen2):  .\Run-Campaign2Host.ps1 -Target documentdb -HostId 1 -StartAtUtc 2026-07-17T06:00:00Z
  #   host 2 (gen1):  .\Run-Campaign2Host.ps1 -Target documentdb -HostId 2 -StartAtUtc 2026-07-17T06:00:00Z
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('documentdb','mongo-shard')][string]$Target,
  [Parameter(Mandatory)][ValidateSet(1,2)][int]$HostId,
  [Parameter(Mandatory)][string]$StartAtUtc,
  [switch]$NoPreflight
)
$ErrorActionPreference = 'Continue'

# ---- PINNED CONSTANTS (identical for every target; do NOT parameterize) ----
$PINNED_CONFIG    = 'config/production/full-workload-open-loop-2host.json'
$PINNED_HOSTCOUNT = 2
$PINNED_SCENARIO  = 'burst'
$PINNED_RUNTAG    = 'openloop-2host-11k'
$PINNED_RESULTS   = 'results'

# ---- Auto-detect environment (works on gen1 Desktop repo and gen2 C:\bmt) ----
$roots = @('C:\bmt','C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench')
$root  = $roots | Where-Object { Test-Path (Join-Path $_ '.git') } | Select-Object -First 1
if (-not $root) { throw 'No repo root found (looked in C:\bmt and the Desktop clone).' }
$dotnet = if (Test-Path 'C:\dotnet\dotnet.exe') { 'C:\dotnet\dotnet.exe' } else { 'dotnet' }
if (Test-Path 'C:\dotnet') { $env:DOTNET_ROOT = 'C:\dotnet' }
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + $env:Path

# ---- Inject the target's connection env var (Machine scope) into THIS process ----
$envVar = if ($Target -eq 'documentdb') { 'BMT_CONN' } else { 'BMT_CONN_MONGO_SHARD' }
$machineVal = [Environment]::GetEnvironmentVariable($envVar,'Machine')
if (-not $machineVal) { throw "Connection env var '$envVar' not set (Machine scope) for target '$Target'." }
Set-Item -Path "Env:$envVar" -Value $machineVal

# ---- Validate the shared start instant ----
$startDt = [DateTimeOffset]::Parse($StartAtUtc,[System.Globalization.CultureInfo]::InvariantCulture,
  [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
$lead = ($startDt - [DateTimeOffset]::UtcNow).TotalSeconds
if ($lead -lt 0) { Write-Warning "start-at is in the PAST; this host will start immediately and may be misaligned with its peer." }

Set-Location $root
Write-Output ("=== CANONICAL 2-host run ===")
Write-Output ("host           = $env:COMPUTERNAME  (host-id $HostId of $PINNED_HOSTCOUNT)")
Write-Output ("target         = $Target  (env $envVar present)")
Write-Output ("repo root      = $root")
Write-Output ("config         = $PINNED_CONFIG  (TaskSleepMs=3500, lambda=5, 150..500 tasks/Job, 3x300s)")
Write-Output ("run-tag        = $PINNED_RUNTAG")
Write-Output ("start-at (UTC) = $($startDt.UtcDateTime.ToString('o'))  (in $([math]::Round($lead,1))s)")
Write-Output ("per-host seed  = 42 + $HostId  (target-independent -> identical offered load across targets)")

$dll = Join-Path $root 'src\Bmt.LoadGen\bin\Release\net8.0\loadgen.dll'
if (-not (Test-Path $dll)) { throw "loadgen.dll not found at $dll — build Release first." }

$args = @(
  'test',
  '--target',     $Target,
  '--scenario',   $PINNED_SCENARIO,
  '--config',     $PINNED_CONFIG,
  '--host-id',    $HostId,
  '--host-count', $PINNED_HOSTCOUNT,
  '--run-tag',    $PINNED_RUNTAG,
  '--start-at',   $StartAtUtc,
  '--results',    $PINNED_RESULTS
)
if ($NoPreflight) { $args += '--no-preflight' }

& $dotnet $dll @args 2>&1 | ForEach-Object { Write-Output $_ }
$runExit = $LASTEXITCODE
Write-Output ("exit=$runExit")
if ($runExit -ne 0) { throw "LoadGen run failed (exit $runExit)." }
Write-Output ("Results under $root\$PINNED_RESULTS  (tag $PINNED_RUNTAG, host $HostId/$PINNED_HOSTCOUNT). Merge with: report merge --input results --tag $PINNED_RUNTAG")
