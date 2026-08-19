<#
.SYNOPSIS
  Server-side metric capture over a completed campaign's run window (handoff Package B5, report §8.7 #3).
  Pulls per-target backend evidence AFTER the run so there is zero load-gen impact and the timestamps
  line up with RunResult.StartedUtc / FinishedUtc.

.DESCRIPTION
  For each -Target, over the [-StartUtc, -EndUtc] window:
    * documentdb  — Azure Monitor metrics for the Cosmos DB for MongoDB *vCore* cluster. Full published
                    set: CpuPercent, MemoryPercent, CommittedMemoryPercent, AutoscaleUtilizationPercent,
                    StoragePercent, StorageUsed, IOPS, NetworkBytesIngress/Egress, MongoRequestDurationMs.
                    MongoRequestDurationMs is also split by Operation (per-op RPS + latency) and by
                    StatusCodeClass (2xx/4xx/5xx counts). Its Count aggregation = server-side requests
                    served (RPS) and the StatusCodeClass dimension surfaces throttles/errors.
                    NOTE: vCore has no dedicated active-connection or 429 counter — concurrent/created
                    connection counts for docdb still come from the client-side merged concurrency; the
                    request count/duration + IOPS confirm the backend actually served the injected load.
    * mongo-shard / mongo-vm —
        - serverStatus + connPoolStats via the bmt_monitor (clusterMonitor) connection
          (BMT_CONN_MONGO_MONITOR), read with the MongoDB .NET driver already built in -RepoDir so no
          mongosh / no secret ever crosses `az vm run-command`. Falls back cleanly if the monitor user
          is unavailable.
        - a mongod.log slice over the window pulled from each backend VM via `az vm run-command`.
        - VM host CPU / available-memory / network via Azure Monitor for each backend VM.

  GUARDED: a clean no-op (writes a skip note, returns) when `az` is not logged in, the resources file is
  missing / unfilled, or the identifiers for a target are empty — so callers never fail because of it.

  Output under -CampaignRoot:
    azure-metrics.json                     consolidated summary (window + per-target rollups)
    metrics-raw/<target>-*.json|.log       raw az output + serverStatus/connPoolStats + mongod.log slice

.PARAMETER CampaignRoot   Campaign results directory to write metrics into. Required.
.PARAMETER StartUtc       Run-window start (UTC). Accepts DateTimeOffset or an ISO-8601 string.
.PARAMETER EndUtc         Run-window end   (UTC). Accepts DateTimeOffset or an ISO-8601 string.
.PARAMETER Targets        Backend keys captured this run (documentdb | mongo-vm | mongo-shard | cosmos-ru).
.PARAMETER AzureResources Path to azure-resources.json. Default: config/azure-resources.json (repo-relative).
.PARAMETER RepoDir        Repo root (for locating the MongoDB driver DLLs). Default: two levels up.
.PARAMETER ResourceGroup  Override the resource group (else taken from azure-resources.json).
.PARAMETER IngestionWaitSeconds
    Seconds to wait BEFORE pulling, so Azure Monitor has ingested the tail of the run window. Azure
    platform metrics lag ~1-5 min — DocumentDB `MongoRequestDurationMs` request Count + its Operation/
    StatusCodeClass dimension splits are the slowest (~5 min). Default 300 (5 min). Set 0 to skip when
    re-pulling an OLD window that is already fully ingested.

.EXAMPLE
  .\Get-AzureMetrics.ps1 -CampaignRoot results\shard-burst -Targets mongo-shard `
      -StartUtc 2026-07-21T18:00:00Z -EndUtc 2026-07-21T18:20:00Z
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$CampaignRoot,
    [Parameter(Mandatory)] $StartUtc,
    [Parameter(Mandatory)] $EndUtc,
    [Parameter(Mandatory)] [ValidateSet('documentdb','mongo-vm','mongo-shard','cosmos-ru')]
    [string[]]$Targets,
    [string]$AzureResources = 'config/azure-resources.json',
    [string]$RepoDir,
    [string]$ResourceGroup,
    [int]$IngestionWaitSeconds = 300
)

$ErrorActionPreference = 'Stop'

if (-not $RepoDir) { $RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
if (-not [System.IO.Path]::IsPathRooted($AzureResources)) { $AzureResources = Join-Path $RepoDir $AzureResources }
if (-not [System.IO.Path]::IsPathRooted($CampaignRoot))   { $CampaignRoot   = Join-Path $RepoDir $CampaignRoot }

function ToUtc($v) {
    if ($v -is [datetimeoffset]) { return $v.ToUniversalTime() }
    return [datetimeoffset]::Parse([string]$v, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
}
$startUtcO = ToUtc $StartUtc
$endUtcO   = ToUtc $EndUtc
$startIso  = $startUtcO.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
$endIso    = $endUtcO.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')

$rawDir = Join-Path $CampaignRoot 'metrics-raw'
$metricsPath = Join-Path $CampaignRoot 'azure-metrics.json'

function Write-Skip([string]$Reason) {
    New-Item -ItemType Directory -Force -Path $CampaignRoot | Out-Null
    [pscustomobject]@{
        runWindowStartUtc = $startIso
        runWindowEndUtc   = $endIso
        targets           = $Targets
        captured          = $false
        skipped           = $true
        reason            = $Reason
    } | ConvertTo-Json -Depth 6 | Set-Content -Path $metricsPath -Encoding utf8
    Write-Host "azure metrics: SKIPPED - $Reason -> $metricsPath" -ForegroundColor DarkYellow
}

# ---- Guards ------------------------------------------------------------------------------------
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { Write-Skip '`az` CLI not found'; return }
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) { Write-Skip 'not logged in (`az login` required)'; return }
if (-not (Test-Path $AzureResources)) { Write-Skip "'$AzureResources' not found"; return }

# Strip // line comments so JSON-with-comments parses on Windows PowerShell 5.1 as well as pwsh 7.
$rawJson = Get-Content $AzureResources -Raw
$stripped = ($rawJson -split "`n" | ForEach-Object { $_ -replace '(?<![:"])//.*$', '' }) -join "`n"
try { $res = $stripped | ConvertFrom-Json } catch { Write-Skip "'$AzureResources' is not valid JSON"; return }

if ([string]::IsNullOrWhiteSpace($res.Subscription)) { Write-Skip 'azure-resources.json has empty Subscription'; return }
if (-not $ResourceGroup) { $ResourceGroup = $res.ResourceGroup }
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) { Write-Skip 'no ResourceGroup (arg or azure-resources.json)'; return }

New-Item -ItemType Directory -Force -Path $rawDir | Out-Null
Write-Host "azure metrics: window $startIso .. $endIso  targets=$($Targets -join ',')" -ForegroundColor Cyan

# ---- Helpers -----------------------------------------------------------------------------------

# Pull a set of Azure Monitor metrics for one resource id and roll up avg/max/min/total/count per metric.
function Get-Metrics {
    param([string]$ResourceId, [string[]]$MetricNames, [string]$RawFile)

    $args = @(
        'monitor','metrics','list',
        '--resource', $ResourceId,
        '--start-time', $startIso,
        '--end-time',   $endIso,
        '--interval',   'PT1M',
        '--metrics'
    ) + $MetricNames + @('--aggregation','Average','Maximum','Minimum','Total','Count','--output','json')

    $rawText = & az @args 2>&1 | Out-String
    Set-Content -Path $RawFile -Value $rawText -Encoding utf8
    $rollup = [ordered]@{}
    try {
        $j = $rawText | ConvertFrom-Json
        foreach ($m in $j.value) {
            $name = $m.name.value
            $pts  = @($m.timeseries.data)
            $avgs = @($pts | Where-Object { $_.average -ne $null } | ForEach-Object { [double]$_.average })
            $maxs = @($pts | Where-Object { $_.maximum -ne $null } | ForEach-Object { [double]$_.maximum })
            $mins = @($pts | Where-Object { $_.minimum -ne $null } | ForEach-Object { [double]$_.minimum })
            $tots = @($pts | Where-Object { $_.total   -ne $null } | ForEach-Object { [double]$_.total })
            $cnts = @($pts | Where-Object { $_.count   -ne $null } | ForEach-Object { [double]$_.count })
            $rollup[$name] = [ordered]@{
                unit    = $m.unit
                avg     = if ($avgs.Count) { [math]::Round(($avgs | Measure-Object -Average).Average, 3) } else { $null }
                max     = if ($maxs.Count) { [math]::Round(($maxs | Measure-Object -Maximum).Maximum, 3) } else { $null }
                min     = if ($mins.Count) { [math]::Round(($mins | Measure-Object -Minimum).Minimum, 3) } else { $null }
                total   = if ($tots.Count) { [math]::Round(($tots | Measure-Object -Sum).Sum, 3) } else { $null }
                count   = if ($cnts.Count) { [math]::Round(($cnts | Measure-Object -Sum).Sum, 0) } else { $null }
                samples = $pts.Count
            }
        }
    } catch {
        $rollup['_error'] = "metric parse failed: $($_.Exception.Message.Split([char]10)[0])"
    }
    return $rollup
}

# Pull ONE metric split by a single dimension (e.g. MongoRequestDurationMs by Operation or by
# StatusCodeClass) and roll up count + avg/max latency per dimension value. This is how DocumentDB
# exposes server-side request throughput (Count of MongoRequestDurationMs = requests served) and
# error/throttle visibility (StatusCodeClass '4xx'/'5xx' counts) — there is no separate connection or
# 429 counter, but the request metric's dimensions carry that information.
function Get-MetricByDimension {
    param([string]$ResourceId, [string]$MetricName, [string]$Dimension, [string]$RawFile)

    $args = @(
        'monitor','metrics','list',
        '--resource', $ResourceId,
        '--start-time', $startIso,
        '--end-time',   $endIso,
        '--interval',   'PT1M',
        '--metrics',    $MetricName,
        '--filter',     "$Dimension eq '*'",
        '--aggregation','Count','Average','Maximum',
        '--top',        '50',
        '--output',     'json'
    )

    $rawText = & az @args 2>&1 | Out-String
    Set-Content -Path $RawFile -Value $rawText -Encoding utf8
    $byDim = [ordered]@{}
    try {
        $j = $rawText | ConvertFrom-Json
        foreach ($m in $j.value) {
            foreach ($ts in $m.timeseries) {
                $dimVal = ($ts.metadatavalues | Where-Object { $_.name.value -ieq $Dimension }).value
                if (-not $dimVal) { $dimVal = '(none)' }
                $pts  = @($ts.data)
                $cnts = @($pts | Where-Object { $_.count   -ne $null } | ForEach-Object { [double]$_.count })
                $avgs = @($pts | Where-Object { $_.average -ne $null } | ForEach-Object { [double]$_.average })
                $maxs = @($pts | Where-Object { $_.maximum -ne $null } | ForEach-Object { [double]$_.maximum })
                $byDim[$dimVal] = [ordered]@{
                    requestCount = if ($cnts.Count) { [math]::Round(($cnts | Measure-Object -Sum).Sum, 0) } else { 0 }
                    avgMs        = if ($avgs.Count) { [math]::Round(($avgs | Measure-Object -Average).Average, 2) } else { $null }
                    maxMs        = if ($maxs.Count) { [math]::Round(($maxs | Measure-Object -Maximum).Maximum, 2) } else { $null }
                }
            }
        }
    } catch {
        $byDim['_error'] = "dimension metric parse failed: $($_.Exception.Message.Split([char]10)[0])"
    }
    return $byDim
}

# Load the MongoDB .NET driver (already built in the repo) and run a command doc against `admin`.
$script:DriverBin = $null
$script:DriverLoaded = $false
function Get-DriverBin {
    if ($script:DriverBin) { return $script:DriverBin }
    $dll = Get-ChildItem (Join-Path $RepoDir 'src') -Recurse -Filter 'MongoDB.Driver.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'Release\\net8\.0' } | Select-Object -First 1
    if (-not $dll) { return $null }
    $script:DriverBin = $dll.DirectoryName
    return $script:DriverBin
}

function Import-MongoDriver {
    if ($script:DriverLoaded) { return $true }
    $bin = Get-DriverBin
    if (-not $bin) { return $false }
    # Preload every DLL in the build output so no on-demand assembly resolution is needed at run time
    # (a ScriptBlock Resolving handler throws "no Runspace" when the driver resolves deps on a
    # threadpool thread). Ignore load errors for native/duplicate assemblies.
    foreach ($f in Get-ChildItem $bin -Filter '*.dll' -ErrorAction SilentlyContinue) {
        try { Add-Type -Path $f.FullName -ErrorAction Stop } catch { }
    }
    $script:DriverLoaded = ('MongoDB.Driver.MongoClient' -as [type]) -ne $null
    return $script:DriverLoaded
}

# Safe BsonValue readers (return $null instead of throwing on a missing/misc-typed field).
function BVInt($doc, [string]$name) {
    try { if ($doc.Contains($name)) { return [int64]$doc[$name].ToInt64() } } catch { }
    return $null
}
function BVStr($doc, [string]$name) {
    try { if ($doc.Contains($name)) { return [string]$doc[$name].ToString() } } catch { }
    return $null
}

# Open ONE client for the monitor connection and run both serverStatus and connPoolStats. The driver
# caches clusters in a process-wide registry keyed by settings, so a single shared client (disposed
# once at the very end) avoids "Cannot access a disposed object" on the second command.
function Get-MongoEvidence {
    param([string]$ConnectionString, [string]$RawDir, [string]$Target)
    if (-not (Import-MongoDriver)) { throw 'MongoDB.Driver.dll not found under <repo>\src (build Release first).' }
    $settings = [MongoDB.Driver.MongoClientSettings]::FromConnectionString($ConnectionString)
    $settings.ServerSelectionTimeout = [TimeSpan]::FromSeconds(20)
    $client = [MongoDB.Driver.MongoClient]::new($settings)
    $out = [ordered]@{}
    try {
        $admin = $client.GetDatabase('admin')
        $ssDoc = [MongoDB.Bson.BsonDocument]::new('serverStatus', 1)
        $ssCmd = [MongoDB.Driver.Command[MongoDB.Bson.BsonDocument]]::op_Implicit($ssDoc)
        $cpDoc = [MongoDB.Bson.BsonDocument]::new('connPoolStats', 1)
        $cpCmd = [MongoDB.Driver.Command[MongoDB.Bson.BsonDocument]]::op_Implicit($cpDoc)
        try {
            $ss = $admin.RunCommand[MongoDB.Bson.BsonDocument]($ssCmd)
            Set-Content -Path (Join-Path $RawDir "$Target-serverStatus.txt") -Value $ss.ToString() -Encoding utf8
            $connCur = $null; $connAvail = $null; $connCreated = $null
            if ($ss.Contains('connections')) {
                $conns = $ss['connections'].AsBsonDocument
                $connCur     = BVInt $conns 'current'
                $connAvail   = BVInt $conns 'available'
                $connCreated = BVInt $conns 'totalCreated'
            }
            $out['serverStatus'] = [ordered]@{
                process            = (BVStr $ss 'process')
                version            = (BVStr $ss 'version')
                connectionsCurrent = $connCur
                connectionsAvail   = $connAvail
                connectionsCreated = $connCreated
            }
        } catch { $out['serverStatusError'] = $_.Exception.Message.Split([char]10)[0] }
        try {
            $cp = $admin.RunCommand[MongoDB.Bson.BsonDocument]($cpCmd)
            Set-Content -Path (Join-Path $RawDir "$Target-connPoolStats.txt") -Value $cp.ToString() -Encoding utf8
            $out['connPoolStats'] = [ordered]@{
                totalInUse     = (BVInt $cp 'totalInUse')
                totalAvailable = (BVInt $cp 'totalAvailable')
                totalCreated   = (BVInt $cp 'totalCreated')
            }
        } catch { $out['connPoolStatsError'] = $_.Exception.Message.Split([char]10)[0] }
    } finally {
        try { $client.Cluster.Dispose() } catch { }
    }
    return $out
}

# Summarize the connection-churn slice of a (possibly multi-GB) mongo log over the window ON the VM,
# returning compact JSON (counts + a small sample) so nothing huge crosses run-command.
function Get-MongoLogSlice {
    param([string]$VmName, [string]$LogPath, [string]$OutFile, [int]$TailLines = 500000)
    $remote = @"
`$ErrorActionPreference='Stop'
`$p = '$LogPath'
if (-not (Test-Path `$p)) { ConvertTo-Json @{ error = "log not found: `$p" } -Compress; return }
`$start = [datetime]::Parse('$startIso').ToUniversalTime()
`$end   = [datetime]::Parse('$endIso').ToUniversalTime()
`$inWindow=0; `$accepted=0; `$ended=0; `$firstTs=`$null; `$lastTs=`$null
`$sample = New-Object System.Collections.Generic.List[string]
foreach (`$line in (Get-Content `$p -Tail $TailLines)) {
  if (`$line -match '"t":\{"\`$date":"([^"]+)"') {
    try { `$ts = [datetimeoffset]::Parse(`$matches[1]).UtcDateTime } catch { continue }
    if (`$ts -ge `$start -and `$ts -le `$end) {
      `$inWindow++
      if (-not `$firstTs) { `$firstTs = `$ts }
      `$lastTs = `$ts
      if (`$line -match '"c":"NETWORK"') {
        if (`$line -match 'Connection accepted') { `$accepted++ }
        elseif (`$line -match 'Connection ended') { `$ended++ }
        if (`$sample.Count -lt 12) { [void]`$sample.Add(`$line.Substring(0, [Math]::Min(300, `$line.Length))) }
      }
    }
  }
}
ConvertTo-Json @{
  logPath           = `$p
  tailLinesScanned  = $TailLines
  linesInWindow     = `$inWindow
  connectionAccepted= `$accepted
  connectionEnded   = `$ended
  windowCoveredFrom = if (`$firstTs) { `$firstTs.ToString('o') } else { `$null }
  windowCoveredTo   = if (`$lastTs)  { `$lastTs.ToString('o') }  else { `$null }
  sample            = `$sample
} -Depth 4 -Compress
"@
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
    $raw = & az vm run-command invoke -g $ResourceGroup -n $VmName --command-id RunPowerShellScript `
        --scripts "powershell -EncodedCommand $enc" -o json 2>&1 | Out-String
    Set-Content -Path $OutFile -Value $raw -Encoding utf8
    # run-command returns the script output wrapped as {"value":[{"message":"[stdout]\n<json>\n[stderr]\n..."}]}
    $msg = $null
    try { $msg = ($raw | ConvertFrom-Json).value[0].message } catch { }
    if (-not $msg) { return [pscustomobject]@{ error = 'run-command returned no message'; raw = $raw.Trim() } }
    $m = [regex]::Match($msg, '(?s)\{.*\}')
    if (-not $m.Success) { return [pscustomobject]@{ error = 'no JSON payload in run-command message'; raw = $msg.Trim() } }
    try { return ($m.Value | ConvertFrom-Json) } catch { return [pscustomobject]@{ error = "log summary parse failed"; raw = $m.Value } }
}

# VM host metrics for a compute VM by name.
function Get-VmHostMetrics {
    param([string]$VmName, [string]$RawFile)
    $vmId = & az vm show -g $ResourceGroup -n $VmName --query id -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($vmId)) { return @{ _error = "vm '$VmName' not found" } }
    return Get-Metrics -ResourceId $vmId `
        -MetricNames @('Percentage CPU','Available Memory Bytes','Network In','Network Out') `
        -RawFile $RawFile
}

# ---- Azure Monitor ingestion lag -----------------------------------------------------------------
# Platform metrics are not queryable until ~1-5 min after the events happen. The slowest is DocumentDB's
# `MongoRequestDurationMs` request Count + Operation/StatusCodeClass dimension splits (~5 min); VM/cluster
# gauges (CPU/mem/net) lag ~1-3 min. Wait here so the TAIL of the run window is populated before we pull.
# Skipped automatically when re-pulling an already-ingested window with -IngestionWaitSeconds 0.
if ($IngestionWaitSeconds -gt 0) {
    Write-Host "azure metrics: waiting ${IngestionWaitSeconds}s for Azure Monitor ingestion (use -IngestionWaitSeconds 0 to skip)..." -ForegroundColor DarkYellow
    Start-Sleep -Seconds $IngestionWaitSeconds
}

# ---- Per-target capture ------------------------------------------------------------------------
$perTarget = [ordered]@{}

foreach ($t in $Targets) {
    Write-Host "azure metrics: capturing $t ..." -ForegroundColor Green
    switch ($t) {
        'documentdb' {
            $rid = $res.DocumentDb.MetricsResourceId
            if ([string]::IsNullOrWhiteSpace($rid)) {
                $perTarget[$t] = @{ captured = $false; reason = 'DocumentDb.MetricsResourceId empty' }
                break
            }
            # Full published metric set for a Cosmos DB for MongoDB vCore cluster (verified via
            # `az monitor metrics list-definitions`): CPU/memory/committed-memory/autoscale/storage
            # saturation, IOPS + network traffic, and end-to-end request duration.
            $roll = Get-Metrics -ResourceId $rid `
                -MetricNames @(
                    'CpuPercent','MemoryPercent','CommittedMemoryPercent','AutoscaleUtilizationPercent',
                    'StoragePercent','StorageUsed','IOPS',
                    'NetworkBytesIngress','NetworkBytesEgress','MongoRequestDurationMs') `
                -RawFile (Join-Path $rawDir 'documentdb-cluster-metrics.json')

            # Server-side throughput + error/throttle visibility: MongoRequestDurationMs carries a
            # request Count (= requests served → RPS) and rich dimensions. Split it by Operation
            # (per-op RPS + latency) and by StatusCodeClass (2xx/4xx/5xx counts — throttles surface here).
            $byOp = Get-MetricByDimension -ResourceId $rid -MetricName 'MongoRequestDurationMs' `
                -Dimension 'Operation' -RawFile (Join-Path $rawDir 'documentdb-request-by-operation.json')
            $byStatus = Get-MetricByDimension -ResourceId $rid -MetricName 'MongoRequestDurationMs' `
                -Dimension 'StatusCodeClass' -RawFile (Join-Path $rawDir 'documentdb-request-by-status.json')

            $perTarget[$t] = [ordered]@{
                captured           = $true
                kind               = 'vcore-cluster'
                cluster            = $res.DocumentDb.ClusterName
                tier               = $res.DocumentDb.Tier
                note               = 'vCore has no active-connection counter, but MongoRequestDurationMs Count = server-side RPS and its StatusCodeClass dimension surfaces 4xx/5xx (throttles). Concurrent/created connection counts still come from the client-side merge.'
                metrics            = $roll
                requestByOperation = $byOp
                requestByStatus    = $byStatus
            }
        }
        { $_ -in 'mongo-shard','mongo-vm' } {
            $node = if ($t -eq 'mongo-shard') { $res.MongoShard } else { $res.MongoVm }
            $entry = [ordered]@{ captured = $true; kind = 'self-managed-mongo' }

            # 1) serverStatus + connPoolStats via bmt_monitor (secret-safe, from THIS host's env var).
            $monConn = [Environment]::GetEnvironmentVariable('BMT_CONN_MONGO_MONITOR')
            if (-not $monConn) { $monConn = [Environment]::GetEnvironmentVariable('BMT_CONN_MONGO_MONITOR','Machine') }
            if ($monConn) {
                try {
                    $ev = Get-MongoEvidence -ConnectionString $monConn -RawDir $rawDir -Target $t
                    foreach ($k in $ev.Keys) { $entry[$k] = $ev[$k] }
                } catch { $entry['monitorError'] = $_.Exception.Message.Split([char]10)[0] }
            } else {
                $entry['monitorNote'] = 'BMT_CONN_MONGO_MONITOR not set; serverStatus/connPoolStats skipped (log slice still captured).'
            }

            # 2) log slice + 3) VM host metrics per backend VM. mongo-shard: client churn lands on the
            #    mongos routers -> slice mongos.log on each router VM. mongo-vm: rs0 mongod.log.
            if ($t -eq 'mongo-shard') { $vmNames = @($node.MongosVmNames); $logPath = $node.MongosLogPath }
            else                      { $vmNames = @($node.ActiveVmName);   $logPath = $node.MongodLogPath }
            $vmNames = $vmNames | Where-Object { $_ } | Select-Object -Unique

            $hostBlock = [ordered]@{}
            foreach ($vm in $vmNames) {
                $logSummary = $null
                try { $logSummary = Get-MongoLogSlice -VmName $vm -LogPath $logPath -OutFile (Join-Path $rawDir "$t-$vm-log-window.json") } catch { $logSummary = [pscustomobject]@{ error = $_.Exception.Message.Split([char]10)[0] } }
                $vmMetrics = Get-VmHostMetrics -VmName $vm -RawFile (Join-Path $rawDir "$t-$vm-host-metrics.json")
                $hostBlock[$vm] = [ordered]@{
                    logSlice = $logSummary
                    host     = $vmMetrics
                }
            }
            $entry['vms'] = $hostBlock
            $perTarget[$t] = $entry
        }
        'cosmos-ru' {
            $perTarget[$t] = @{ captured = $false; reason = 'cosmos-ru server RU capture out of scope this round' }
        }
    }
}

# ---- Consolidated output -----------------------------------------------------------------------
[pscustomobject]@{
    runWindowStartUtc = $startIso
    runWindowEndUtc   = $endIso
    capturedUtc       = [datetimeoffset]::UtcNow.UtcDateTime.ToString('o')
    subscription      = $res.Subscription
    resourceGroup     = $ResourceGroup
    targets           = $Targets
    captured          = $true
    perTarget         = $perTarget
} | ConvertTo-Json -Depth 12 | Set-Content -Path $metricsPath -Encoding utf8

Write-Host "azure metrics: wrote $metricsPath (+ raw under $rawDir)" -ForegroundColor Cyan
