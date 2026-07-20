<#
.SYNOPSIS
  Run the loadgen preflight for one target on a generator VM, injecting the target's Machine-scope
  connection env var into the process (run-command's agent env may predate the var being set).
  Example: az vm run-command invoke ... --scripts @scripts/run/Invoke-Preflight.ps1 --parameters Target=mongo-vm
#>
param([Parameter(Mandatory)][string]$Target)
$env:DOTNET_ROOT = 'C:\dotnet'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine')
$envVar = switch ($Target) {
    'documentdb'  { 'BMT_CONN' }
    'mongo-vm'    { 'BMT_CONN_MONGO' }
    'mongo-shard' { 'BMT_CONN_MONGO_SHARD' }
    'cosmos-ru'   { 'BMT_CONN_COSMOS' }
}
$machineVal = [Environment]::GetEnvironmentVariable($envVar,'Machine')
if ($machineVal) { Set-Item -Path "Env:$envVar" -Value $machineVal }
Set-Location 'C:\bmt'
$dll = 'C:\bmt\src\Bmt.Preflight\bin\Release\net8.0\preflight.dll'
$cfg = 'config/production/full-workload-open-loop-multihost.json'
Write-Output ("=== preflight target=$Target (env $envVar present=$([bool]$machineVal)) ===")
& 'C:\dotnet\dotnet.exe' $dll --config $cfg --target $Target 2>&1 | ForEach-Object { Write-Output $_ }
Write-Output ("exit=$LASTEXITCODE")
