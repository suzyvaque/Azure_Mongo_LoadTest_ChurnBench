<#
.SYNOPSIS
  Item 8 (config-only, no new VM): raise the mongos TCP accept backlog so the connection-accept queue
  does not overflow during the 3-host cold-connection storm — the overflow that surfaces as the
  intermittent `ServerSelectionTimeout` on 10.3.0.4:27016 during preflight under load. Runs ON a mongos
  VM (invoke via `az vm run-command` on each router VM). Idempotent; backs up the config once; restarts
  the mongos service so the new backlog applies.

  NOTE: this does NOT remove the root ~4.3k concurrency ceiling — that is mongos/mongod VM CPU saturation
  (per-connection TLS+SCRAM), which only more/dedicated router capacity fixes. A larger listen backlog
  reduces accept-queue overflow (fewer selection-timeout blips / iteration retries), a cheaper partial
  mitigation chosen in place of provisioning a dedicated mongos VM.

.PARAMETER Target       Desired net.listenBacklog value. Default 4096 (Windows SOMAXCONN is 0x7fffffff, so
                        the mongos backlog is the effective cap; 4096 comfortably absorbs the burst).
.PARAMETER CfgPath      mongos config path. Default E:\mongo\config\mongos.cfg.
.PARAMETER ServiceName  mongos Windows service. Default mongo-mongos.
#>
param(
    [int]$Target = 4096,
    [string]$CfgPath = 'E:\mongo\config\mongos.cfg',
    [string]$ServiceName = 'mongo-mongos'
)

if (-not (Test-Path $CfgPath)) { Write-Output "MISSING_CFG $CfgPath"; exit 1 }
$bak = "$CfgPath.bak-listenbacklog"
if (-not (Test-Path $bak)) { Copy-Item $CfgPath $bak -Force; Write-Output "backup -> $bak" }

$lines = Get-Content $CfgPath
$before = ($lines | Where-Object { $_ -match 'listenBacklog' }) -join ' | '
Write-Output "before: $(if ($before) { $before } else { '(no listenBacklog set; using default)' })"

if ($lines -match 'listenBacklog') {
    # Replace existing value.
    $new = $lines -replace '(\s*listenBacklog:\s*)\d+', "`${1}$Target"
} else {
    # Insert under the net: section (2-space indent to match the YAML block).
    $new = foreach ($l in $lines) {
        $l
        if ($l -match '^\s*net:\s*$') { "  listenBacklog: $Target" }
    }
}
Set-Content -Path $CfgPath -Value $new -Encoding ASCII
$after = (Get-Content $CfgPath | Where-Object { $_ -match 'listenBacklog' }) -join ' | '
Write-Output "after:  $after"

Write-Output "restarting service $ServiceName ..."
Restart-Service -Name $ServiceName -Force
Start-Sleep -Seconds 8
$svc = Get-Service -Name $ServiceName
Write-Output ("service $ServiceName status = " + $svc.Status)
