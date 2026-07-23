<#
.SYNOPSIS
  Orchestrates a coordinated multi-host open-loop burst campaign across all co-located generator VMs
  for one target, from an operator box with Azure CLI (test_instruction.md §6.2 Track C).

.DESCRIPTION
  Picks the generator pool for the target (same-AZ VMs), computes a single shared --start-at UTC instant
  (now + LeadSeconds), then fires Run-BurstHost.ps1 on every host CONCURRENTLY via `az vm run-command
  invoke`, passing an incrementing --host-id, the shared --host-count, --run-tag and --start-at. Because
  all hosts share the same start instant, their bursts align and the combined conn/s + concurrency sum
  in the same wall-clock second even though the invocations don't fire at the exact same millisecond.

  Generator pools (deployed AZ1 topology, koreacentral zone 1; override with -HostVms):
    documentdb  -> AZ1: vm-hpc-loadgen-az1-0, vm-hpc-loadgen-az1-1, vm-hpc-loadgen-az1-2
    mongo-vm    -> AZ1: vm-hpc-loadgen-az1-0, vm-hpc-loadgen-az1-1, vm-hpc-loadgen-az1-2
    mongo-shard -> AZ1: vm-hpc-loadgen-az1-0, vm-hpc-loadgen-az1-1, vm-hpc-loadgen-az1-2
  (runs are sequential, so every target shares the same AZ1 trio; host-count = 3).

  Connection strings are NOT passed here — each host reads its own machine env var (set once per host,
  see runbook STEP 4), keeping secrets out of run-command logs.

  After all hosts finish (and -PushResults on each host pushed to the shared repo), run Merge-Campaign.ps1
  to prove the ≥1,200 conn/s / ≥11,000 concurrent envelope was reached.

.PARAMETER Target        documentdb | mongo-vm | mongo-shard | cosmos-ru.
.PARAMETER RunTag        Shared campaign tag. Default: <target>-<yyyyMMdd-HHmmss>.
.PARAMETER LeadSeconds   Seconds from now until the shared timed-phase start. Default 120 (allow build + preflight).
.PARAMETER ResourceGroup RG holding the generator VMs. Default: rg-db-test-hpc.
.PARAMETER HostVms       Explicit ordered VM-name list (overrides the deduced pool). Host-id = position.
.PARAMETER Config        Config path passed to each host.
.PARAMETER Scenario      steady | burst | both. Default burst.
.PARAMETER RepoDir       Repo root on each host. Default C:\bmt.
.PARAMETER PushResults   Tell each host to git-push its results (recommended for later merge).
.PARAMETER NoPreflight   Pass --no-preflight to each host (NOT recommended).

.EXAMPLE
  # 2-host DocumentDB burst starting 2 minutes from now:
  .\Invoke-Campaign.ps1 -Target documentdb -RunTag docdb-m80-burst -PushResults
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('documentdb','mongo-vm','mongo-shard','cosmos-ru')]
    [string]$Target,
    [string]$RunTag,
    [int]$LeadSeconds = 120,
    [string]$ResourceGroup = 'rg-db-test-hpc',
    [string[]]$HostVms,
    [string]$Config = 'config/production/full-workload-open-loop-3host.json',
    [ValidateSet('steady','burst','both')] [string]$Scenario = 'burst',
    [string]$RepoDir = 'C:\bmt',
    [switch]$PushResults,
    [switch]$NoPreflight
)

$ErrorActionPreference = 'Stop'

# Compact base-36 stamp (last <MaxChars> chars) — mirrors RunOrchestrator.Base36Suffix so the campaign
# tag and each host's result folder share the same start-derived stamp.
function ConvertTo-Base36Suffix {
    param([long]$Value, [int]$MaxChars = 3)
    if ($Value -le 0) { return '0' }
    $chars = '0123456789abcdefghijklmnopqrstuvwxyz'
    $s = ''
    while ($Value -gt 0) {
        $s = $chars[[int]($Value % 36)] + $s
        $Value = [long][math]::Floor($Value / 36)
    }
    if ($s.Length -le $MaxChars) { return $s }
    return $s.Substring($s.Length - $MaxChars)
}

# ---- Resolve the same-AZ generator pool for this target ----
if (-not $HostVms -or $HostVms.Count -eq 0) {
    # All targets run sequentially from the same AZ1 trio (koreacentral zone 1).
    $az1Trio = @('vm-hpc-loadgen-az1-0', 'vm-hpc-loadgen-az1-1', 'vm-hpc-loadgen-az1-2')
    $HostVms = switch ($Target) {
        'documentdb'  { $az1Trio }
        'mongo-vm'    { $az1Trio }
        'mongo-shard' { $az1Trio }
        'cosmos-ru'   { $az1Trio }
    }
}
$hostCount = $HostVms.Count

# ---- Single shared start instant for every host ----
$startInstant = [DateTimeOffset]::UtcNow.AddSeconds($LeadSeconds)
$startAt = $startInstant.ToString('yyyy-MM-ddTHH:mm:ssZ')

# ---- Default campaign tag: <db>-<MMdd>-<stamp>. Shares the date + base-36 start stamp with each host's
#      compact result folder (<db>-<loop>-<workload>-<MMdd>-<stamp>) so operator + per-host artifacts
#      correlate at a glance. Pass -RunTag to override. ----
if (-not $RunTag) {
    $dbLabel = switch ($Target) {
        'mongo-shard' { 'mongo' }
        'mongo-vm'    { 'mongovm' }
        'documentdb'  { 'docdb' }
        'cosmos-ru'   { 'cosmos' }
        default       { $Target }
    }
    $stamp = ConvertTo-Base36Suffix -Value $startInstant.ToUnixTimeSeconds() -MaxChars 3
    $RunTag = "$dbLabel-$($startInstant.ToString('MMdd'))-$stamp"
}

Write-Host "==== Multi-host burst campaign ====" -ForegroundColor Cyan
Write-Host "  target     : $Target"
Write-Host "  run-tag    : $RunTag"
Write-Host "  host-count : $hostCount"
Write-Host "  hosts      : $($HostVms -join ', ')"
Write-Host "  start-at   : $startAt  (T+${LeadSeconds}s)"
Write-Host "  config     : $Config"
Write-Host "===================================" -ForegroundColor Cyan

$scriptPath = "$RepoDir\scripts\run\Run-BurstHost.ps1"
$pushFlag   = if ($PushResults) { '-PushResults' } else { '' }
$noPfFlag   = if ($NoPreflight) { '-NoPreflight' } else { '' }

# ---- Campaign-level server-side artifact dir on THIS operator box (az1-0). Holds the in-run
#      serverStatus timeseries + the post-run azure-metrics.json. Committed separately by the operator
#      (kept off the per-host push path to avoid git contention in C:\bmt during the run). ----
$campaignRoot = Join-Path $RepoDir "results\_campaign-$RunTag"
New-Item -ItemType Directory -Force -Path $campaignRoot | Out-Null
$stopFile = Join-Path $campaignRoot '.sampler-stop'
if (Test-Path $stopFile) { Remove-Item $stopFile -Force }

# ---- Start the in-run server-side connection/opcounters sampler (self-managed mongo targets only;
#      documentdb vCore publishes no connection metric). Read-only serverStatus, negligible load.
#      Guarded: a failure here never aborts the campaign. ----
$samplerJob = $null
if ($Target -in @('mongo-shard', 'mongo-vm')) {
    try {
        $monConn = [Environment]::GetEnvironmentVariable('BMT_CONN_MONGO_MONITOR')
        if (-not $monConn) { $monConn = [Environment]::GetEnvironmentVariable('BMT_CONN_MONGO_MONITOR', 'Machine') }
        if ($monConn) {
            $samplerScript = "$RepoDir\scripts\run\Sample-MongoServerStats.ps1"
            $samplerCsv    = Join-Path $campaignRoot 'server-samples\mongo-serverstats.csv'
            $maxDur        = $LeadSeconds + 1200
            $samplerJob = Start-Job -Name "sampler-$RunTag" -ScriptBlock {
                param($sp, $conn, $csv, $stop, $repo, $maxDur)
                & $sp -ConnectionString $conn -OutCsv $csv -IntervalSeconds 5 -MaxDurationSeconds $maxDur -StopFile $stop -RepoDir $repo
            } -ArgumentList $samplerScript, $monConn, $samplerCsv, $stopFile, $RepoDir, $maxDur
            Write-Host "[sampler] server-side serverStatus timeseries -> $samplerCsv" -ForegroundColor DarkCyan
        } else {
            Write-Host "[sampler] BMT_CONN_MONGO_MONITOR not set; skipping server-side sampler." -ForegroundColor DarkYellow
        }
    } catch {
        Write-Host "[sampler] failed to start (continuing without it): $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

# ---- Fire each host concurrently via az vm run-command (each as a background job) ----
$jobs = @()
for ($i = 0; $i -lt $hostCount; $i++) {
    $vm     = $HostVms[$i]
    $hostId = $i + 1

    # The inline script run ON the host: invokes Run-BurstHost.ps1 with this host's parameters. Kept on a
    # SINGLE line — az vm run-command reassembles the --scripts payload and drops the newlines that PS
    # backtick line-continuations depend on, which otherwise fails with "Incomplete string token".
    $remote = "& '$scriptPath' -Target '$Target' -HostId $hostId -HostCount $hostCount -RunTag '$RunTag' -StartAtUtc '$startAt' -Config '$Config' -Scenario '$Scenario' -RepoDir '$RepoDir' $pushFlag $noPfFlag"

    Write-Host "[launch] host $hostId/$hostCount -> $vm" -ForegroundColor Green
    $jobs += Start-Job -Name "burst-$vm" -ScriptBlock {
        param($rg, $vmName, $remoteScript)
        az vm run-command invoke `
            --resource-group $rg `
            --name $vmName `
            --command-id RunPowerShellScript `
            --scripts $remoteScript `
            --output json 2>&1
    } -ArgumentList $ResourceGroup, $vm, $remote
}

Write-Host "All $hostCount hosts launched. Waiting for run-command completion (may take 15-25 min)..." -ForegroundColor Cyan
$jobs | Wait-Job | Out-Null

# Timed load has now ended on every host. Mark the run-window end and stop the in-run sampler.
$windowEnd = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
if ($samplerJob) {
    try {
        New-Item -ItemType File -Force -Path $stopFile | Out-Null
        Wait-Job $samplerJob -Timeout 30 | Out-Null
        Receive-Job $samplerJob -ErrorAction SilentlyContinue | Out-Null
    } catch { } finally {
        Stop-Job $samplerJob -ErrorAction SilentlyContinue
        Remove-Job $samplerJob -Force -ErrorAction SilentlyContinue
        Remove-Item $stopFile -Force -ErrorAction SilentlyContinue
    }
    $csvPath = Join-Path $campaignRoot 'server-samples\mongo-serverstats.csv'
    if (Test-Path $csvPath) {
        $rows = @(Import-Csv $csvPath)
        $peak = ($rows | Where-Object { $_.connCurrent -match '^\d+$' } |
            Measure-Object -Property connCurrent -Maximum).Maximum
        Write-Host "[sampler] captured $($rows.Count) rows; server-side peak connCurrent (per router) = $peak" -ForegroundColor DarkCyan
    }
}

foreach ($j in $jobs) {
    Write-Host "---- $($j.Name) ----" -ForegroundColor Yellow
    Receive-Job $j
    Remove-Job $j
}

# ---- Auto-pull server-side Azure Monitor + mongo evidence over the run window (guarded no-op if az
#      is not logged in / resources file unfilled). Correct window: shared timed start .. load end. ----
Write-Host ""
Write-Host "Pulling server-side metrics over [$startAt .. $windowEnd] ..." -ForegroundColor Cyan
try {
    & "$RepoDir\scripts\run\Get-AzureMetrics.ps1" `
        -CampaignRoot $campaignRoot `
        -StartUtc $startAt -EndUtc $windowEnd `
        -Targets $Target -RepoDir $RepoDir
} catch {
    Write-Host "azure metrics pull failed (non-fatal): $($_.Exception.Message)" -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "Campaign '$RunTag' dispatched to all hosts." -ForegroundColor Green
Write-Host "Server-side artifacts: $campaignRoot" -ForegroundColor Green
Write-Host "Next: once each host has pushed its results/, run:" -ForegroundColor Green
Write-Host "  .\Merge-Campaign.ps1 -RunTag $RunTag -InputDir <results-dir-with-all-hosts>" -ForegroundColor Green
