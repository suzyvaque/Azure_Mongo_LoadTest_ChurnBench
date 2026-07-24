<#
.SYNOPSIS
  Live sharded-cluster topology + router-health diagnostic (read-only, RunCommand-only).
  Answers: are BOTH mongos routers live & responsive? are BOTH shards in the cluster?
  Reads BMT_CONN_MONGO_MONITOR (env; never printed). Build Release first.
#>
[CmdletBinding()]
param(
    [string]$ConnEnv = 'BMT_CONN_MONGO_MONITOR',
    [string]$RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)
$ErrorActionPreference = 'Stop'

$conn = [Environment]::GetEnvironmentVariable($ConnEnv,'Process')
if (-not $conn) { $conn = [Environment]::GetEnvironmentVariable($ConnEnv,'User') }
if (-not $conn) { $conn = [Environment]::GetEnvironmentVariable($ConnEnv,'Machine') }
if (-not $conn) { throw "$ConnEnv not set." }

$dll = Get-ChildItem (Join-Path $RepoDir 'src') -Recurse -Filter 'MongoDB.Driver.dll' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Release\\net8\.0' } | Select-Object -First 1
if (-not $dll) { throw 'MongoDB.Driver.dll not found (build Release first).' }
foreach ($f in Get-ChildItem $dll.DirectoryName -Filter '*.dll') { try { Add-Type -Path $f.FullName -EA Stop } catch {} }

function RunDb($client,[string]$db,[string]$json) {
    $database = $client.GetDatabase($db)
    $bd = [MongoDB.Bson.BsonDocument]::Parse($json)
    $cmd = [MongoDB.Driver.BsonDocumentCommand[MongoDB.Bson.BsonDocument]]::new($bd)
    if (-not $script:_runMi) {
        $script:_runMi = [MongoDB.Driver.IMongoDatabase].GetMethods() |
            Where-Object { $_.Name -eq 'RunCommand' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 3 } |
            Select-Object -First 1
    }
    $gm = $script:_runMi.MakeGenericMethod([MongoDB.Bson.BsonDocument])
    return $gm.Invoke($database, @($cmd, $null, [System.Threading.CancellationToken]::None))
}

$base = [MongoDB.Driver.MongoClientSettings]::FromConnectionString($conn)
$base.ServerSelectionTimeout = [TimeSpan]::FromSeconds(15)
$seeds = @($base.Servers)
Write-Host ("seeds in monitor conn: {0}" -f (($seeds | ForEach-Object { "$($_.Host):$($_.Port)" }) -join ', '))

Write-Host "`n==== CLUSTER-WIDE (mongos topology) ===="
$cluster = [MongoDB.Driver.MongoClient]::new($base)

try {
    $ls = RunDb $cluster 'admin' '{ listShards: 1 }'
    $arr = $ls['shards'].AsBsonArray
    Write-Host "-- listShards (count=$($arr.Count)) --"
    foreach ($s in $arr) { $d=$s.AsBsonDocument; Write-Host ("  shard _id={0} host={1} state={2} draining={3}" -f $d['_id'],$d['host'],($(if($d.Contains('state')){$d['state']}else{'n/a'})),($(if($d.Contains('draining')){$d['draining']}else{'false'}))) }
} catch { Write-Host "  listShards FAILED: $($_.Exception.Message)" }

try {
    $fm = RunDb $cluster 'config' '{ find: "mongos", limit: 20 }'
    $batch = $fm['cursor'].AsBsonDocument['firstBatch'].AsBsonArray
    Write-Host "`n-- config.mongos registered routers (count=$($batch.Count)) --"
    $now = [DateTime]::UtcNow
    foreach ($m in $batch) {
        $md=$m.AsBsonDocument
        $ping = if ($md.Contains('ping')) { [DateTime]$md['ping'].ToUniversalTime() } else { [DateTime]::MinValue }
        $age = [math]::Round(($now - $ping).TotalSeconds,0)
        $ver = if ($md.Contains('mongoVersion')) { $md['mongoVersion'] } else { '?' }
        Write-Host ("  router={0} v{1} lastPing={2}s ago  {3}" -f $md['_id'],$ver,$age,($(if($age -le 60){'LIVE'}else{'STALE/DOWN'})))
    }
} catch { Write-Host "  config.mongos read FAILED: $($_.Exception.Message)" }

# Per-seed HELLO ping timing (TCP-listening vs app-responsive) + serverStatus.
Write-Host "`n-- per-router health (direct, hello timing) --"
foreach ($srv in $seeds) {
    $key = "$($srv.Host):$($srv.Port)"
    $s = $base.Clone(); $s.Server = $srv; $s.DirectConnection = $true; $s.ServerSelectionTimeout = [TimeSpan]::FromSeconds(8)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $c = [MongoDB.Driver.MongoClient]::new($s)
        $h = RunDb $c 'admin' '{ hello: 1 }'
        $sw.Stop()
        $msg = if ($h.Contains('msg')) { $h['msg'] } else { '(no msg)' }
        try {
            $ss = RunDb $c 'admin' '{ serverStatus: 1 }'
            $cn = $ss['connections'].AsBsonDocument
            $line = ("cur={0} avail={1} created={2}" -f $cn['current'],$cn['available'],$cn['totalCreated'])
            $proc = $ss['process']
        } catch { $line = "serverStatus err: $($_.Exception.Message)"; $proc='?' }
        Write-Host ("  {0}  hello={1}ms process={2} msg={3}  {4}" -f $key, $sw.ElapsedMilliseconds, $proc, $msg, $line)
        $c.Cluster.Dispose()
    } catch {
        $sw.Stop()
        Write-Host ("  {0}  UNRESPONSIVE after {1}ms: {2}" -f $key, $sw.ElapsedMilliseconds, $_.Exception.Message.Split([Environment]::NewLine)[0])
    }
}

$cluster.Cluster.Dispose()
Write-Host "`n==== done ===="
