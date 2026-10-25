<#
Build a per-minute time-aligned correlation table for one campaign iteration.
Joins the 3 per-host client timeseries CSVs (per-second) bucketed to UTC minute
with the Azure per-minute platform metrics (CpuPercent, MemoryPercent, IOPS,
MongoRequestDurationMs avg/max + server RPS from its Count).

Output: a CSV with one row per UTC minute:
  minuteUtc,
  loadgen_conn_attempted_per_s, loadgen_conn_ready_per_s, loadgen_conn_failed,
  loadgen_active_ready_avg (concurrency), loadgen_failed_ops,
  az_server_rps (MongoReqDuration Count/60), az_mongo_req_ms_avg, az_mongo_req_ms_max,
  az_cpu_avg, az_cpu_max, az_mem_avg, az_iops_avg, az_iops_max
#>
param(
  [Parameter(Mandatory)][string]$RunDir,   # e.g. results/docdb-tier-test/m80/run-1
  [Parameter(Mandatory)][string]$OutCsv
)

$ErrorActionPreference = 'Stop'

# --- client per-second -> per-minute (summed across the 3 hosts) ---
$clientCsvs = Get-ChildItem (Join-Path $RunDir 'loadgen') -Filter '*timeseries*.csv'
$byMin = @{}   # key = unix minute -> accumulator
foreach ($c in $clientCsvs) {
  # derive host start unix-seconds from the sibling json (StartedUnixSeconds)
  $json = Get-ChildItem (Join-Path $RunDir 'loadgen') -Filter ($c.Name -replace '-timeseries\.csv$','.json') | Select-Object -First 1
  $start = ([long]((Get-Content $json.FullName -Raw | ConvertFrom-Json).StartedUnixSeconds))
  Import-Csv $c.FullName | ForEach-Object {
    $absSec = $start + [int]$_.second
    $minKey = [long]([math]::Floor($absSec / 60) * 60)
    if (-not $byMin.ContainsKey($minKey)) {
      $byMin[$minKey] = [ordered]@{ conn_created=0; conn_ready=0; conn_failed=0; failed_ops=0; ar_sum=0.0; secset=(New-Object 'System.Collections.Generic.HashSet[long]') }
    }
    $a = $byMin[$minKey]
    $a.conn_created += [int]$_.conn_created
    $a.conn_ready   += [int]$_.conn_ready
    $a.conn_failed  += [int]$_.conn_failed
    $a.failed_ops   += [int]$_.failed_ops
    $a.ar_sum       += [double]$_.active_ready
    [void]$a.secset.Add([long]$absSec)
  }
}

# --- Azure per-minute ---
$az = Get-Content (Join-Path $RunDir 'azure\metrics-raw\documentdb-cluster-metrics.json') -Raw | ConvertFrom-Json
function Get-Series($metricName) {
  $m = $az.value | Where-Object { $_.name.value -eq $metricName }
  $h = @{}
  $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
  foreach ($d in $m.timeseries[0].data) {
    $t = [datetimeoffset]::Parse($d.timeStamp, [System.Globalization.CultureInfo]::InvariantCulture, $styles).ToUnixTimeSeconds()
    $h[[long]$t] = $d
  }
  return $h
}
$cpu  = Get-Series 'CpuPercent'
$mem  = Get-Series 'MemoryPercent'
$iops = Get-Series 'IOPS'
$mrd  = Get-Series 'MongoRequestDurationMs'

$rows = @()
$allMin = ($byMin.Keys + $cpu.Keys) | Sort-Object -Unique
foreach ($mk in $allMin) {
  $a = $byMin[[long]$mk]
  $cpuD = $cpu[[long]$mk]; $memD = $mem[[long]$mk]; $ioD = $iops[[long]$mk]; $mrdD = $mrd[[long]$mk]
  $rows += [pscustomobject][ordered]@{
    minuteUtc                    = ([datetimeoffset]::FromUnixTimeSeconds([long]$mk)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
    lg_conn_attempted_per_s      = if ($a) { [math]::Round($a.conn_created/60,1) } else { 0 }
    lg_conn_ready_per_s          = if ($a) { [math]::Round($a.conn_ready/60,1) } else { 0 }
    lg_conn_failed               = if ($a) { $a.conn_failed } else { 0 }
    lg_active_ready_avg          = if ($a -and $a.secset.Count) { [math]::Round($a.ar_sum/$a.secset.Count,0) } else { 0 }
    lg_failed_ops                = if ($a) { $a.failed_ops } else { 0 }
    az_server_rps                = if ($mrdD) { [math]::Round($mrdD.count/60,0) } else { $null }
    az_mongo_req_ms_avg          = if ($mrdD) { [math]::Round($mrdD.average,3) } else { $null }
    az_mongo_req_ms_max          = if ($mrdD) { [math]::Round($mrdD.maximum,0) } else { $null }
    az_cpu_avg                   = if ($cpuD) { [math]::Round($cpuD.average,2) } else { $null }
    az_cpu_max                   = if ($cpuD) { [math]::Round($cpuD.maximum,2) } else { $null }
    az_mem_avg                   = if ($memD) { [math]::Round($memD.average,2) } else { $null }
    az_iops_avg                  = if ($ioD) { [math]::Round($ioD.average,0) } else { $null }
    az_iops_max                  = if ($ioD) { [math]::Round($ioD.maximum,0) } else { $null }
  }
}
$rows = $rows | Sort-Object minuteUtc
$rows | Export-Csv -NoTypeInformation -Path $OutCsv
Write-Host "Wrote $OutCsv ($($rows.Count) minute rows)"
$rows | Format-Table -AutoSize
