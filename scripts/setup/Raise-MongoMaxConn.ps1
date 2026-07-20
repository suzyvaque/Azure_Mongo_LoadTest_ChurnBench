<#
.SYNOPSIS
  Raise net.maxIncomingConnections on the rs0 shardsvr mongod (VM1) from 5000 to a target value and
  restart the service so the new ceiling applies. Idempotent: sets the value to $Target regardless of
  current value; backs up the config once. All other mongo endpoints (both mongos, VM2 shard2) already
  default to 65536, so only this file caps below the >11,000-concurrent requirement.
#>
param([int]$Target = 20000, [string]$CfgPath = 'C:\Program Files\MongoDB\Server\7.0\bin\mongod.cfg', [string]$ServiceName = 'MongoDB')

if (-not (Test-Path $CfgPath)) { Write-Output "MISSING_CFG $CfgPath"; exit 1 }
$bak = "$CfgPath.bak-maxconn"
if (-not (Test-Path $bak)) { Copy-Item $CfgPath $bak -Force; Write-Output "backup -> $bak" }

$lines = Get-Content $CfgPath
$before = ($lines | Where-Object { $_ -match 'maxIncomingConnections' }) -join ' | '
Write-Output "before: $before"

if ($lines -match 'maxIncomingConnections') {
  $new = $lines -replace '(\s*maxIncomingConnections:\s*)\d+', "`${1}$Target"
} else {
  # insert under the net: section
  $new = foreach ($l in $lines) { $l; if ($l -match '^\s*net:\s*$') { "  maxIncomingConnections: $Target" } }
}
Set-Content -Path $CfgPath -Value $new -Encoding ASCII
$after = (Get-Content $CfgPath | Where-Object { $_ -match 'maxIncomingConnections' }) -join ' | '
Write-Output "after:  $after"

Write-Output "restarting service $ServiceName ..."
Restart-Service -Name $ServiceName -Force
Start-Sleep -Seconds 8
$svc = Get-Service -Name $ServiceName
Write-Output ("service $ServiceName status = " + $svc.Status)
