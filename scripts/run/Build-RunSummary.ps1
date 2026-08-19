<#
.SYNOPSIS
  Item 4/5: build an aggregated mongo-vs-documentdb markdown summary for ONE grouped run folder
  (results/run-{date}-{num}/) produced by a sequential campaign. Reads each target's per-iteration,
  per-host result JSON, computes combined concurrency + latency + CPU/mem + retry telemetry, and always
  discloses the DocumentDB-SRV vs mongo-direct-pin access-path asymmetry (Item 5).

.DESCRIPTION
  Expects the grouped layout:
     <RunFolder>/mongo/iter-NN/*.json         (per-host files, -hN in filename for multi-host)
     <RunFolder>/docdb/iter-NN/*.json
  Optionally reads server-side CPU/mem from <RunFolder>/<target>/_server-metrics.json (schema:
  { cpuAvg, cpuPeak, memPct } or the Get-AzureMetrics output) when present.

  Combined concurrency = per-absolute-second SUM of each host's driver ActiveReady gauge aligned by
  StartedUnixSeconds + per-second index (the `report merge` convention). Latency percentiles are the mean
  of each contributing file's percentile. Output: <RunFolder>/summary-<run>-documentdb-vs-mongo.md.

.PARAMETER RunFolder     Path to results/run-{date}-{num}.
.PARAMETER MongoDir      Subfolder name for the mongo target. Default 'mongo'.
.PARAMETER DocdbDir      Subfolder name for the documentdb target. Default 'docdb'.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunFolder,
    [string]$MongoDir = 'mongo',
    [string]$DocdbDir = 'docdb'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $RunFolder)) { throw "RunFolder not found: $RunFolder" }

function Get-TargetStats {
    param([string]$Dir)
    if (-not (Test-Path $Dir)) { return $null }
    $files = Get-ChildItem $Dir -Recurse -Filter *.json -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*aggregate*' -and $_.Name -notlike '*_server-metrics*' }
    if (-not $files) { return $null }

    $iters = @{}       # iterNumber -> list of host result objects
    foreach ($f in $files) {
        $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
        $n = [int]$j.IterationNumber
        if (-not $iters.ContainsKey($n)) { $iters[$n] = @() }
        $iters[$n] += $j
    }

    $perIter = @()
    foreach ($n in ($iters.Keys | Sort-Object)) {
        $hosts = $iters[$n]
        # Combined concurrency: per-absolute-second SUM of ActiveReady across hosts.
        $bySec = @{}
        foreach ($h in $hosts) {
            $base = [long]$h.StartedUnixSeconds
            $ar = @($h.Throughput | ForEach-Object { [int]$_.ActiveReady })
            for ($i = 0; $i -lt $ar.Count; $i++) {
                $sec = $base + $i
                $bySec[$sec] = [long]($bySec[$sec]) + [long]$ar[$i]
            }
        }
        $sums = @($bySec.Values)
        $maxConn = if ($sums.Count) { ($sums | Measure-Object -Maximum).Maximum } else { 0 }
        $avgConn = if ($sums.Count) { [math]::Round(($sums | Measure-Object -Average).Average, 1) } else { 0 }

        function MeanP($prop90, $prop99, $selector) {
            $p90 = @($hosts | ForEach-Object { [double](& $selector $_).P90Ms })
            $p99 = @($hosts | ForEach-Object { [double](& $selector $_).P99Ms })
            return @{
                p90 = [math]::Round(($p90 | Measure-Object -Average).Average, 1)
                p99 = [math]::Round(($p99 | Measure-Object -Average).Average, 1)
            }
        }
        $conn = MeanP $null $null { param($x) $x.ConnectionOpenMs }
        $cyc  = MeanP $null $null { param($x) $x.TaskCycleLatencyMs }
        $fi   = MeanP $null $null { param($x) $x.OperationLatencyMs.find_input }
        $rem  = MeanP $null $null { param($x) $x.OperationLatencyMs.remove }
        $ins  = MeanP $null $null { param($x) $x.OperationLatencyMs.insert }
        $fo   = MeanP $null $null { param($x) $x.OperationLatencyMs.find_output }

        $tps = [math]::Round((@($hosts | ForEach-Object { [double]$_.Totals.SuccessfulTasks / [double]$_.Arrival.MeasuredArrivalDurationSeconds }) | Measure-Object -Sum).Sum, 1)
        $errPct = [math]::Round((@($hosts | ForEach-Object { if ($_.Totals.TotalTasks) { 100.0 * $_.Totals.FailedTasks / $_.Totals.TotalTasks } else { 0 } }) | Measure-Object -Average).Average, 3)
        $cpuPeak = [math]::Round((@($hosts | ForEach-Object { [double]$_.Process.MaxCpuPercent }) | Measure-Object -Maximum).Maximum, 1)
        $wsPeakMB = [math]::Round((@($hosts | ForEach-Object { [double]$_.Process.PeakWorkingSetBytes / 1MB }) | Measure-Object -Maximum).Maximum, 0)
        $retryEnabled = [bool]($hosts[0].Retry.RetryWritesEnabled)
        $retryFail = (@($hosts | ForEach-Object { [long]$_.Retry.RetryableCommandFailures }) | Measure-Object -Sum).Sum
        $warmupSec = [math]::Round((@($hosts | ForEach-Object { [double]$_.WarmupSeconds }) | Measure-Object -Average).Average, 1)
        $warmupDocs = [long]($hosts[0].WarmupDocCount)
        $perHostPeak = @($hosts | ForEach-Object { [int]$_.Lifecycle.PeakActiveReady }) -join ' / '

        $perIter += [pscustomobject]@{
            Iter = $n; Hosts = $hosts.Count; MaxConn = $maxConn; AvgConn = $avgConn; PerHostPeak = $perHostPeak
            Tps = $tps; ErrPct = $errPct; Conn = $conn; Cycle = $cyc; Find = $fi; Remove = $rem; Insert = $ins; FindOut = $fo
            CpuPeak = $cpuPeak; WsPeakMB = $wsPeakMB; RetryEnabled = $retryEnabled; RetryFail = $retryFail
            WarmupSec = $warmupSec; WarmupDocs = $warmupDocs
        }
    }
    if (-not $perIter) { return $null }

    function MeanOf($sel) { [math]::Round((@($perIter | ForEach-Object { & $sel $_ }) | Measure-Object -Average).Average, 1) }
    $firstIter = ($iters.Keys | Sort-Object | Select-Object -First 1)
    $target = $iters[$firstIter][0].Target
    $srv = [pscustomobject]@{
        Target = $target
        Iters = $perIter.Count
        MaxConn = ($perIter | Measure-Object MaxConn -Maximum).Maximum
        MeanMaxConn = MeanOf { param($x) $x.MaxConn }
        AvgConn = MeanOf { param($x) $x.AvgConn }
        PerIter = $perIter
        Tps = MeanOf { param($x) $x.Tps }
        ErrPct = [math]::Round((@($perIter | ForEach-Object { $_.ErrPct }) | Measure-Object -Average).Average, 3)
        Conn90 = MeanOf { param($x) $x.Conn.p90 }; Conn99 = MeanOf { param($x) $x.Conn.p99 }
        Cyc90 = MeanOf { param($x) $x.Cycle.p90 }; Cyc99 = MeanOf { param($x) $x.Cycle.p99 }
        Find90 = MeanOf { param($x) $x.Find.p90 }; Find99 = MeanOf { param($x) $x.Find.p99 }
        Rem90 = MeanOf { param($x) $x.Remove.p90 }; Rem99 = MeanOf { param($x) $x.Remove.p99 }
        Ins90 = MeanOf { param($x) $x.Insert.p90 }; Ins99 = MeanOf { param($x) $x.Insert.p99 }
        Fo90 = MeanOf { param($x) $x.FindOut.p90 }; Fo99 = MeanOf { param($x) $x.FindOut.p99 }
        CpuPeak = MeanOf { param($x) $x.CpuPeak }; WsPeakMB = MeanOf { param($x) $x.WsPeakMB }
        RetryEnabled = $perIter[0].RetryEnabled
        RetryFail = (@($perIter | ForEach-Object { $_.RetryFail }) | Measure-Object -Sum).Sum
        WarmupSec = MeanOf { param($x) $x.WarmupSec }
        WarmupDocs = $perIter[0].WarmupDocs
    }
    return $srv
}

$mongo = Get-TargetStats (Join-Path $RunFolder $MongoDir)
$docdb = Get-TargetStats (Join-Path $RunFolder $DocdbDir)
if (-not $mongo -and -not $docdb) { throw "No result JSON found under $RunFolder\{$MongoDir,$DocdbDir}." }

function Cell($v) { if ($null -eq $v) { 'n/a' } else { $v } }
$runName = Split-Path $RunFolder -Leaf
$sb = [System.Text.StringBuilder]::new()
$null = $sb.AppendLine("# Aggregated summary — documentdb vs mongo-shard ($runName)")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') from ``$RunFolder``. Latency in ms; percentiles are the mean of contributing per-host/per-iteration values. Concurrency is the combined per-second SUM of each host's driver ActiveReady (the ``report merge`` convention).")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("> **Access-path disclosure (Item 5).** mongo-shard Tasks are pinned round-robin to a single ``mongos`` router (``directConnection=true``) to avoid the per-client SDAM topology-monitor thread explosion under no-reuse churn. DocumentDB is a single managed **SRV/gateway** endpoint, so there is **no equivalent optimization to apply** (no multi-node topology to monitor; forcing directConnection would defeat gateway routing). **These results therefore compare each backend's production ACCESS PATH — mongo direct-to-router vs DocumentDB SRV gateway — not pure database-engine internals.**")
$null = $sb.AppendLine("")

# ---- Max/Avg connections ----
$null = $sb.AppendLine("## Max / Avg concurrent connections (combined across hosts)")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("| Target | Iters | Max conn (best) | Max conn (mean) | Avg conn (mean) | Per-host peak (last iter) |")
$null = $sb.AppendLine("|---|---|---|---|---|---|")
foreach ($t in @($docdb, $mongo)) {
    if ($t) { $null = $sb.AppendLine("| $($t.Target) | $($t.Iters) | $($t.MaxConn) | $($t.MeanMaxConn) | $($t.AvgConn) | $($t.PerIter[-1].PerHostPeak) |") }
}
$null = $sb.AppendLine("")

# ---- Latency ----
$null = $sb.AppendLine("## Latency — p90 / p99 (ms), mean of iterations")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("| Metric | documentdb | mongo-shard |")
$null = $sb.AppendLine("|---|---|---|")
$rows = @(
    @('Connection (TCP+TLS+auth)', { param($t) "$($t.Conn90) / $($t.Conn99)" }),
    @('End-to-end cycle',          { param($t) "$($t.Cyc90) / $($t.Cyc99)" }),
    @('find (cold, op1)',          { param($t) "$($t.Find90) / $($t.Find99)" }),
    @('remove (warm)',             { param($t) "$($t.Rem90) / $($t.Rem99)" }),
    @('insert (warm)',             { param($t) "$($t.Ins90) / $($t.Ins99)" }),
    @('find (warm)',               { param($t) "$($t.Fo90) / $($t.Fo99)" })
)
foreach ($r in $rows) {
    $d = if ($docdb) { & $r[1] $docdb } else { 'n/a' }
    $m = if ($mongo) { & $r[1] $mongo } else { 'n/a' }
    $null = $sb.AppendLine("| $($r[0]) | $d | $m |")
}
$null = $sb.AppendLine("")

# ---- Headline + CPU/mem + retry ----
$null = $sb.AppendLine("## Headline, client CPU/memory, and retry telemetry")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("| Metric | documentdb | mongo-shard |")
$null = $sb.AppendLine("|---|---|---|")
$hrows = @(
    @('Throughput (tasks/s, combined)', { param($t) "$($t.Tps)" }),
    @('Error rate (%)',                 { param($t) "$($t.ErrPct)" }),
    @('Client CPU peak (%)',            { param($t) "$($t.CpuPeak)" }),
    @('Client working set peak (MB)',   { param($t) "$($t.WsPeakMB)" }),
    @('Warm-up time (s, all docs)',     { param($t) "$($t.WarmupSec)  ($($t.WarmupDocs) docs)" }),
    @('Retry writes enabled',           { param($t) "$($t.RetryEnabled)" }),
    @('Retryable command failures',     { param($t) "$($t.RetryFail)" })
)
foreach ($r in $hrows) {
    $d = if ($docdb) { & $r[1] $docdb } else { 'n/a' }
    $m = if ($mongo) { & $r[1] $mongo } else { 'n/a' }
    $null = $sb.AppendLine("| $($r[0]) | $d | $m |")
}
$null = $sb.AppendLine("")
$null = $sb.AppendLine("### Per-iteration concurrency")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("| Target | Iter | Max conn | Avg conn | Per-host peak |")
$null = $sb.AppendLine("|---|---|---|---|---|")
foreach ($t in @($docdb, $mongo)) {
    if ($t) { foreach ($p in $t.PerIter) { $null = $sb.AppendLine("| $($t.Target) | $($p.Iter) | $($p.MaxConn) | $($p.AvgConn) | $($p.PerHostPeak) |") } }
}
$null = $sb.AppendLine("")
$null = $sb.AppendLine("> Retryable-write telemetry (Item 1): ``RetryWritesEnabled`` reflects the driver setting (forced ON for documentdb, ON for mongo, OFF for cosmos-ru). ``Retryable command failures`` counts workload command failures carrying a retryable condition — cross-check against throttling/429 metrics.")

$outFile = Join-Path $RunFolder "summary-$runName-documentdb-vs-mongo.md"
[System.IO.File]::WriteAllText($outFile, $sb.ToString())
Write-Host "Wrote $outFile" -ForegroundColor Green
