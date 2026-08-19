<#
Runs ONE open-loop production-RATE campaign (~1,210 conn/s, TaskSleepMs=9000, full 4-op) on a SINGLE
loadgen host to reproduce the production connection shape (§4b-2) on the current DocumentDB cluster.
Deploys the config to C:\bmt first, invokes Run-BurstHost single-host scenario=burst, then emits a compact
metric bundle matching the §4b-2 rows (throughput, conn p90/p99, find-cold p90/p99, insert p99, cycle p99,
succ/fail/tot, conn-open fails, max concurrent, client CPU).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidateSet('documentdb','mongo-shard')][string]$Target,
    [Parameter(Mandatory)][string]$ResultsDir,
    [Parameter(Mandatory)][string]$CampaignName,
    [string]$Vm = 'vm-hpc-loadgen-az1-0',
    [string]$ResourceGroup = 'rg-db-test-hpc',
    [int]$LeadSeconds = 300,
    [string]$RepoDir = 'C:\bmt'
)
$ErrorActionPreference = 'Stop'
$localCfg = 'C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench\config\production\full-workload-open-loop-prodrate-1host.json'
$cfgRel = 'config/production/full-workload-open-loop-prodrate-1host.json'

# 1. Deploy the config file to the host repo (base64, avoids depending on git-on-host).
$cfgB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content $localCfg -Raw)))
$deploy = "`$p='$RepoDir\$($cfgRel -replace '/','\\')'; [IO.File]::WriteAllBytes(`$p,[Convert]::FromBase64String('$cfgB64')); Write-Output ('deployed '+`$p+' bytes='+((Get-Item `$p).Length))"
$rd = az vm run-command invoke -g $ResourceGroup --name $Vm --command-id RunPowerShellScript --scripts $deploy -o json 2>$null | ConvertFrom-Json
Write-Host "  $($rd.value[0].message)"

# 2. Invoke Run-BurstHost single-host, burst scenario, open-loop prod-rate config.
$startAt = ([DateTimeOffset]::UtcNow.AddSeconds($LeadSeconds)).ToString('yyyy-MM-ddTHH:mm:ssZ')
$scriptPath = "$RepoDir\scripts\run\Run-BurstHost.ps1"
$remote = "& '$scriptPath' -Target '$Target' -HostId 1 -HostCount 1 -RunTag '$Tag' -StartAtUtc '$startAt' -Config '$cfgRel' -Scenario 'burst' -RepoDir '$RepoDir' -ResultsDir '$ResultsDir' -CampaignName '$CampaignName'"
Write-Host ">>> open-loop prod-rate  tag=$Tag  target=$Target  start-at=$startAt (T+${LeadSeconds}s)"
$r = az vm run-command invoke -g $ResourceGroup --name $Vm --command-id RunPowerShellScript --scripts $remote -o json 2>$null | ConvertFrom-Json
$msg = ($r.value[0].message) + "`n[STDERR]`n" + ($r.value[1].message)
$ok = ($msg -match 'run complete\.') -and ($msg -notmatch 'Exception|Unhandled error|LoadGen run failed')
Write-Host "  $Vm : $(if($ok){'COMPLETE'}else{'FAILED'})"
if (-not $ok) { Write-Host "  --- tail ---`n$($msg.Substring([Math]::Max(0,$msg.Length-2000)))"; return }

# 3. Collect compact metrics (all iterations; §4b-2 rows).
$emit = "`$f=Get-ChildItem -Recurse 'C:\bmt\results' -Filter *.json -EA SilentlyContinue | Where-Object { `$_.Name -notlike '*aggregate*' -and `$_.Name -notlike '*compact*' -and `$_.Name -notmatch 'latency|timeseries|target-tcp' }; `$rows=@(); foreach(`$x in `$f){ try{ `$j=Get-Content `$x.FullName -Raw|ConvertFrom-Json }catch{continue}; if(`$j.RunTag -eq '$Tag'){ `$op=`$j.OperationLatencyMs; `$co=`$j.ConnectionOpenMs; `$cy=`$j.TaskCycleLatencyMs; `$lc=`$j.Lifecycle; `$o=[ordered]@{ iter=[int]`$j.IterationNumber; dur=[double]`$j.DurationSeconds; succ=[long]`$j.Totals.SuccessfulTasks; fail=[long]`$j.Totals.FailedTasks; tot=[long]`$j.Totals.TotalTasks; fi90=[double]`$op.find_input.P90Ms; fi99=[double]`$op.find_input.P99Ms; ins99=[double]`$op.insert.P99Ms; conn90=[double]`$co.P90Ms; conn99=[double]`$co.P99Ms; cyc99=[double]`$cy.P99Ms; connFail=[long]`$lc.ConnectionsFailed; maxConc=[int]`$lc.PeakActiveReady; connCreated=[long]`$lc.ConnectionsCreated; cpuMax=[double]`$j.Process.MaxCpuPercent; ports=[int]`$j.Process.PeakEphemeralPortsInUse }; `$rows+=(New-Object psobject -Property `$o) } }; Write-Output ('###M###'+((`$rows|Sort-Object iter)|ConvertTo-Json -Compress -Depth 4)+'###E###')"
$r2 = az vm run-command invoke -g $ResourceGroup --name $Vm --command-id RunPowerShellScript --scripts $emit -o json 2>$null | ConvertFrom-Json
$m2 = $r2.value[0].message
if ($m2 -match '###M###(.*?)###E###') {
    $payload = $Matches[1]
    $dir = Join-Path $ResultsDir $CampaignName
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -Path (Join-Path $dir 'compact.json') -Value $payload -Encoding utf8
    $rows = $payload | ConvertFrom-Json
    foreach ($row in @($rows)) {
        $tps = [math]::Round($row.succ/$row.dur,1)
        $err = if($row.tot){[math]::Round(100.0*$row.fail/$row.tot,2)}else{0}
        Write-Host ("  iter $($row.iter): tps=$tps err%=$err maxConc=$($row.maxConc) connFail=$($row.connFail) ports=$($row.ports)  find p90/99=$([math]::Round($row.fi90,0))/$([math]::Round($row.fi99,0))  cyc p99=$([math]::Round($row.cyc99,0))")
    }
}
Write-Host "OPENLOOP_DONE $Tag"
