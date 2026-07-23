<#
.SYNOPSIS
  In-run server-side sampler for self-managed MongoDB (Package B5, gap-fill). Polls serverStatus on
  EACH mongos router directly every -IntervalSeconds and appends a CSV row per router per tick, so a
  campaign captures the TRUE server-side concurrent-connection timeseries + peak and a server-side
  QPS (opcounters) timeseries. This is the independent confirmation of the client-side InFlightMax
  concurrency claim (the post-run serverStatus in Get-AzureMetrics reads ~idle because load drained).

.DESCRIPTION
  Reads the bmt_monitor (clusterMonitor) connection string, splits its seed list, and opens ONE
  direct-connection client per mongos so connections.current is attributed to the right router. Runs
  read-only `serverStatus` — no writes, negligible load — until -StopFile appears or -MaxDurationSeconds
  elapses (safety cap so it never runs forever if the orchestrator dies). Secret-safe: the connection
  string is passed in-process only, never over az vm run-command.

  CSV columns:
    timestampUtc,host,connCurrent,connAvailable,connActive,connTotalCreated,
    opInsert,opQuery,opUpdate,opDelete,opGetmore,opCommand
  connCurrent is the live concurrent-connection gauge; opcounters are cumulative (difference adjacent
  rows for per-interval QPS). Sum connCurrent across the two routers for cluster-wide concurrency.

.PARAMETER ConnectionString  bmt_monitor connection string (multi-seed mongos). Required.
.PARAMETER OutCsv            CSV path to append samples to (created with header if absent). Required.
.PARAMETER IntervalSeconds   Poll period. Default 5.
.PARAMETER MaxDurationSeconds Safety cap on total run time. Default 2400 (40 min).
.PARAMETER StopFile          Sentinel path; sampling stops promptly once this file exists.
.PARAMETER RepoDir           Repo root for locating the MongoDB driver DLLs. Default: two levels up.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ConnectionString,
    [Parameter(Mandatory)] [string]$OutCsv,
    [int]$IntervalSeconds = 5,
    [int]$MaxDurationSeconds = 2400,
    [string]$StopFile,
    [string]$RepoDir
)

$ErrorActionPreference = 'Stop'
if (-not $RepoDir) { $RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }

# ---- Load the MongoDB .NET driver already built in the repo (preload every DLL so no on-demand
#      assembly resolution runs on a threadpool thread, which throws "no Runspace available"). ----
$dll = Get-ChildItem (Join-Path $RepoDir 'src') -Recurse -Filter 'MongoDB.Driver.dll' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Release\\net8\.0' } | Select-Object -First 1
if (-not $dll) { throw 'MongoDB.Driver.dll not found under <repo>\src (build Release first).' }
foreach ($f in Get-ChildItem $dll.DirectoryName -Filter '*.dll' -ErrorAction SilentlyContinue) {
    try { Add-Type -Path $f.FullName -ErrorAction Stop } catch { }
}

# ---- One direct-connection client per mongos router (so connections.current is per-router). ----
$base = [MongoDB.Driver.MongoClientSettings]::FromConnectionString($ConnectionString)
$clients = [ordered]@{}
foreach ($srv in @($base.Servers)) {
    $s = $base.Clone()
    $s.Server = $srv
    $s.DirectConnection = $true
    $s.ServerSelectionTimeout = [TimeSpan]::FromSeconds(10)
    $clients["$($srv.Host):$($srv.Port)"] = [MongoDB.Driver.MongoClient]::new($s)
}
if ($clients.Count -eq 0) { throw 'no mongos seeds parsed from connection string' }

$dir = Split-Path -Parent $OutCsv
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
if (-not (Test-Path $OutCsv)) {
    Set-Content -Path $OutCsv -Encoding utf8 `
        -Value 'timestampUtc,host,connCurrent,connAvailable,connActive,connTotalCreated,opInsert,opQuery,opUpdate,opDelete,opGetmore,opCommand'
}

function BInt($doc, [string]$name) {
    try { if ($doc.Contains($name)) { return [int64]$doc[$name].ToInt64() } } catch { }
    return $null
}

$deadline = (Get-Date).AddSeconds($MaxDurationSeconds)
$ssDoc = [MongoDB.Bson.BsonDocument]::new('serverStatus', 1)
$ssCmd = [MongoDB.Driver.Command[MongoDB.Bson.BsonDocument]]::op_Implicit($ssDoc)

try {
    while ((Get-Date) -lt $deadline) {
        if ($StopFile -and (Test-Path $StopFile)) { break }
        $ts = [datetimeoffset]::UtcNow.UtcDateTime.ToString('o')
        foreach ($hostKey in @($clients.Keys)) {
            try {
                $ss = $clients[$hostKey].GetDatabase('admin').RunCommand[MongoDB.Bson.BsonDocument]($ssCmd)
                $cCur = $cAvail = $cAct = $cCreated = $null
                if ($ss.Contains('connections')) {
                    $c = $ss['connections'].AsBsonDocument
                    $cCur = BInt $c 'current'; $cAvail = BInt $c 'available'
                    $cAct = BInt $c 'active';  $cCreated = BInt $c 'totalCreated'
                }
                $oIns = $oQry = $oUpd = $oDel = $oGm = $oCmd = $null
                if ($ss.Contains('opcounters')) {
                    $o = $ss['opcounters'].AsBsonDocument
                    $oIns = BInt $o 'insert'; $oQry = BInt $o 'query'; $oUpd = BInt $o 'update'
                    $oDel = BInt $o 'delete'; $oGm = BInt $o 'getmore'; $oCmd = BInt $o 'command'
                }
                $row = @($ts, $hostKey, $cCur, $cAvail, $cAct, $cCreated, $oIns, $oQry, $oUpd, $oDel, $oGm, $oCmd) -join ','
                Add-Content -Path $OutCsv -Value $row -Encoding utf8
            } catch {
                Add-Content -Path $OutCsv -Value "$ts,$hostKey,ERR,,,,,,,,," -Encoding utf8
            }
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
} finally {
    foreach ($c in $clients.Values) { try { $c.Cluster.Dispose() } catch { } }
}
