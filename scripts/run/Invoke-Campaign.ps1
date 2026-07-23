<#
.SYNOPSIS
  Coordinator that OWNS the synchronized multi-host iteration loop for one target (test_instruction.md
  §6.2 Track C / §1). Runs from an operator box with Azure CLI.

.DESCRIPTION
  Picks the generator pool for the target (same-AZ VMs) and OWNS the iteration loop centrally. For EACH
  iteration it:
    1. Computes ONE fresh shared --start-at UTC instant (now + lead), 30-60s in the future.
    2. Launches EXACTLY ONE iteration on every host CONCURRENTLY via `az vm run-command invoke`,
       passing --host-id, the shared --host-count, --run-tag, --iteration-number, --iteration-count and
       the shared --start-at.
    3. Waits for ALL hosts' remote executions to finish, INCLUDING drain.
    4. Validates that every required host reported a completed run for that iteration.
    5. Only then advances to the next iteration (with a brand-new shared start instant).
  If ANY host fails an iteration, the WHOLE three-host iteration is re-run (never continued with only
  two hosts); prior artifacts are preserved and the run tag/iteration number are reused so `report merge`
  keeps the latest complete set. Because all hosts share one per-iteration start instant, their bursts
  align and combined conn/s + concurrency sum in the same wall-clock second.

  Generator pools (deployed AZ1 topology, koreacentral zone 1; override with -HostVms):
    documentdb  -> AZ1: vm-hpc-loadgen-az1-0, vm-hpc-loadgen-az1-1, vm-hpc-loadgen-az1-2
    mongo-vm    -> AZ1: vm-hpc-loadgen-az1-0, vm-hpc-loadgen-az1-1, vm-hpc-loadgen-az1-2
    mongo-shard -> AZ1: vm-hpc-loadgen-az1-0, vm-hpc-loadgen-az1-1, vm-hpc-loadgen-az1-2
  (runs are sequential, so every target shares the same AZ1 trio; host-count = 3).

  Connection strings are NOT passed here — each host reads its own machine env var (set once per host,
  see runbook STEP 4), keeping secrets out of run-command logs.

  After all iterations complete (and -PushResults on each host pushed to the shared repo), run
  Merge-Campaign.ps1 to prove the ≥1,200 conn/s / ≥11,000 concurrent envelope was reached per iteration.

.PARAMETER Target        documentdb | mongo-vm | mongo-shard | cosmos-ru.
.PARAMETER RunTag        Shared campaign tag. Default: <target>-<yyyyMMdd-HHmmss>.
.PARAMETER Iterations    Number of synchronized iterations the coordinator drives. Default 3.
.PARAMETER LeadSeconds   Seconds from now until the FIRST iteration's shared start. Default 120 (allow build + preflight).
.PARAMETER InterIterationLeadSeconds Seconds of lead for each subsequent iteration's shared start (drain settle + skew guard, 30-60s). Default 45.
.PARAMETER MaxAttemptsPerIteration   Max attempts to get a complete three-host iteration before aborting. Default 3.
.PARAMETER ResourceGroup RG holding the generator VMs. Default: rg-db-test-hpc.
.PARAMETER HostVms       Explicit ordered VM-name list (overrides the deduced pool). Host-id = position.
.PARAMETER Config        Config path passed to each host.
.PARAMETER Scenario      steady | burst | both. Default burst.
.PARAMETER RepoDir       Repo root on each host. Default C:\bmt.
.PARAMETER PushResults   Tell each host to git-push its results (recommended for later merge).
.PARAMETER NoPreflight   Pass --no-preflight to each host (NOT recommended).

.EXAMPLE
  # 3-iteration DocumentDB burst campaign, hosts synchronized per iteration:
  .\Invoke-Campaign.ps1 -Target documentdb -RunTag docdb-m80-burst -Iterations 3 -PushResults
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('documentdb','mongo-vm','mongo-shard','cosmos-ru')]
    [string]$Target,
    [string]$RunTag,
    [int]$Iterations = 3,
    [int]$LeadSeconds = 120,
    [int]$InterIterationLeadSeconds = 45,
    [int]$MaxAttemptsPerIteration = 3,
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

# ---- Base instant used ONLY to derive the default campaign tag. Each iteration computes its OWN fresh
#      shared start instant inside the loop below (§1: never reuse a stale start across iterations). ----
$tagInstant = [DateTimeOffset]::UtcNow.AddSeconds($LeadSeconds)

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
    $stamp = ConvertTo-Base36Suffix -Value $tagInstant.ToUnixTimeSeconds() -MaxChars 3
    $RunTag = "$dbLabel-$($tagInstant.ToString('MMdd'))-$stamp"
}

Write-Host "==== Multi-host synchronized campaign ====" -ForegroundColor Cyan
Write-Host "  target      : $Target"
Write-Host "  run-tag     : $RunTag"
Write-Host "  host-count  : $hostCount"
Write-Host "  hosts       : $($HostVms -join ', ')"
Write-Host "  iterations  : $Iterations  (coordinator-owned; fresh shared start per iteration)"
Write-Host "  lead        : first=+${LeadSeconds}s subsequent=+${InterIterationLeadSeconds}s"
Write-Host "  config      : $Config"
Write-Host "==========================================" -ForegroundColor Cyan

$scriptPath = "$RepoDir\scripts\run\Run-BurstHost.ps1"
$pushFlag   = if ($PushResults) { '-PushResults' } else { '' }
$noPfFlag   = if ($NoPreflight) { '-NoPreflight' } else { '' }

# ---- Launch ONE synchronized iteration across all hosts and validate completeness. Returns a hashtable
#      with Ok (all hosts completed), StartAt, and per-host outputs. A host is considered complete only
#      when its run-command output contains Run-BurstHost's terminal success marker AND shows no
#      unhandled exception — the coordinator's validation gate for §1 (never continue with two hosts). ----
function Invoke-CampaignIteration {
    param([int]$IterationNumber, [int]$TotalIterations, [datetimeoffset]$StartInstant)

    $startAt = $StartInstant.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $lead = [int]([math]::Round(($StartInstant - [DateTimeOffset]::UtcNow).TotalSeconds))
    Write-Host ""
    Write-Host ">>> Iteration $IterationNumber/$TotalIterations  start-at=$startAt (T+${lead}s) <<<" -ForegroundColor Cyan

    $jobs = @()
    for ($i = 0; $i -lt $hostCount; $i++) {
        $vm     = $HostVms[$i]
        $hostId = $i + 1

        # Single-line remote payload (az vm run-command drops newlines that PS backtick continuations need).
        $remote = "& '$scriptPath' -Target '$Target' -HostId $hostId -HostCount $hostCount -RunTag '$RunTag' -StartAtUtc '$startAt' -IterationNumber $IterationNumber -IterationCount $TotalIterations -Config '$Config' -Scenario '$Scenario' -RepoDir '$RepoDir' $pushFlag $noPfFlag"

        Write-Host "[launch] iter $IterationNumber host $hostId/$hostCount -> $vm" -ForegroundColor Green
        $jobs += Start-Job -Name "burst-$vm-i$IterationNumber" -ScriptBlock {
            param($rg, $vmName, $remoteScript)
            az vm run-command invoke `
                --resource-group $rg `
                --name $vmName `
                --command-id RunPowerShellScript `
                --scripts $remoteScript `
                --output json 2>&1
        } -ArgumentList $ResourceGroup, $vm, $remote
    }

    Write-Host "All $hostCount hosts launched for iteration $IterationNumber. Waiting for completion..." -ForegroundColor Cyan
    $jobs | Wait-Job | Out-Null

    $hostResults = @()
    $allOk = $true
    foreach ($j in $jobs) {
        $out = (Receive-Job $j | Out-String)
        Remove-Job $j
        # Validate: Run-BurstHost prints "[host N/M] run complete." on success; any unhandled exception
        # or a failed run surfaces as an error record / "Exception" in the run-command output.
        $completed = $out -match 'run complete\.'
        $errored   = $out -match 'Exception|Unhandled error|run failed'
        $ok = $completed -and (-not $errored)
        if (-not $ok) { $allOk = $false }
        Write-Host "---- $($j.Name): $(if ($ok) { 'COMPLETE' } else { 'FAILED/INCOMPLETE' }) ----" -ForegroundColor $(if ($ok) { 'Yellow' } else { 'Red' })
        Write-Host $out
        $hostResults += [pscustomobject]@{ Job = $j.Name; Ok = $ok; Output = $out }
    }

    return @{ Ok = $allOk; StartAt = $startAt; Hosts = $hostResults }
}

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
            # Cover every iteration: first lead + N iterations of (~inter-iteration lead + ~600s window).
            $maxDur        = $LeadSeconds + ($Iterations * ($InterIterationLeadSeconds + 1200))
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

# ---- Coordinator-owned iteration loop: each iteration gets a FRESH shared start instant, launches all
#      hosts once, waits for full completion (incl. drain), and validates every host reported complete.
#      A failed iteration is re-run in full (never continued with a partial host set); artifacts from the
#      failed attempt are preserved on the hosts. Only after a complete iteration do we advance. ----
$campaignStartAt = $null
$windowEnd = $null
$iterationLog = @()
for ($iter = 1; $iter -le $Iterations; $iter++) {
    $complete = $false
    for ($attempt = 1; $attempt -le $MaxAttemptsPerIteration; $attempt++) {
        $lead = if ($iter -eq 1 -and $attempt -eq 1) { $LeadSeconds } else { $InterIterationLeadSeconds }
        $startInstant = [DateTimeOffset]::UtcNow.AddSeconds($lead)
        if (-not $campaignStartAt) { $campaignStartAt = $startInstant.ToString('yyyy-MM-ddTHH:mm:ssZ') }

        if ($attempt -gt 1) {
            Write-Host "[retry] iteration $iter attempt $attempt/$MaxAttemptsPerIteration (previous attempt had a failed/incomplete host)." -ForegroundColor Yellow
        }

        $res = Invoke-CampaignIteration -IterationNumber $iter -TotalIterations $Iterations -StartInstant $startInstant
        $windowEnd = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $iterationLog += [pscustomobject]@{
            Iteration = $iter; Attempt = $attempt; StartAt = $res.StartAt; Ok = $res.Ok
            Hosts = ($res.Hosts | ForEach-Object { "$($_.Job)=$(if ($_.Ok) {'ok'} else {'fail'})" }) -join ' '
        }

        if ($res.Ok) {
            Write-Host "[iteration $iter] COMPLETE on all $hostCount hosts (attempt $attempt)." -ForegroundColor Green
            $complete = $true
            break
        }

        Write-Host "[iteration $iter] INCOMPLETE (a host failed) on attempt $attempt. Artifacts preserved; re-running the full three-host iteration." -ForegroundColor Red
    }

    if (-not $complete) {
        # Persist the iteration log before aborting so the operator can see what happened.
        $iterationLog | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $campaignRoot 'iteration-log.json')
        throw "Iteration $iter could not complete on all $hostCount hosts after $MaxAttemptsPerIteration attempts. Aborting campaign (never continue with a partial host set)."
    }
}

# Record the coordinator's per-iteration outcome for the operator + later merge sanity-check.
$iterationLog | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $campaignRoot 'iteration-log.json')

# All iterations complete on every host. Stop the in-run sampler.
if (-not $windowEnd) { $windowEnd = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }
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

# ---- Auto-pull server-side Azure Monitor + mongo evidence over the whole run window (guarded no-op if
#      az is not logged in / resources file unfilled). Window: first iteration start .. last load end. ----
Write-Host ""
Write-Host "Pulling server-side metrics over [$campaignStartAt .. $windowEnd] ..." -ForegroundColor Cyan
try {
    & "$RepoDir\scripts\run\Get-AzureMetrics.ps1" `
        -CampaignRoot $campaignRoot `
        -StartUtc $campaignStartAt -EndUtc $windowEnd `
        -Targets $Target -RepoDir $RepoDir
} catch {
    Write-Host "azure metrics pull failed (non-fatal): $($_.Exception.Message)" -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "Campaign '$RunTag' complete: $Iterations synchronized iteration(s) on all $hostCount hosts." -ForegroundColor Green
Write-Host "Server-side artifacts: $campaignRoot" -ForegroundColor Green
Write-Host "Next: once each host has pushed its results/, run:" -ForegroundColor Green
Write-Host "  .\Merge-Campaign.ps1 -RunTag $RunTag -InputDir <results-dir-with-all-hosts>" -ForegroundColor Green
