<#
.SYNOPSIS
  Portable preflight runner: detects the repo root (C:\bmt or the Desktop clone) and dotnet
  (C:\dotnet or PATH), injects the target's Machine-scope conn env var into the process, and runs the
  preflight against the 2-host open-loop config. Works on both gen1 (Desktop repo, system dotnet) and
  gen2 (C:\bmt, C:\dotnet) hosts.
    az vm run-command invoke ... --scripts @scripts/multihost/Invoke-Preflight-Portable.ps1 --parameters Target=mongo-shard
#>
param([Parameter(Mandatory)][string]$Target)
$roots = @('C:\bmt','C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench')
$root = $roots | Where-Object { Test-Path (Join-Path $_ '.git') } | Select-Object -First 1
if (-not $root) { Write-Output 'NO_REPO_ROOT'; exit 1 }
$dotnet = if (Test-Path 'C:\dotnet\dotnet.exe') { 'C:\dotnet\dotnet.exe' } else { 'dotnet' }
if (Test-Path 'C:\dotnet') { $env:DOTNET_ROOT = 'C:\dotnet' }
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + $env:Path
$envVar = switch ($Target) {
  'documentdb'  { 'BMT_CONN' }
  'mongo-vm'    { 'BMT_CONN_MONGO' }
  'mongo-shard' { 'BMT_CONN_MONGO_SHARD' }
  'cosmos-ru'   { 'BMT_CONN_COSMOS' }
}
$machineVal = [Environment]::GetEnvironmentVariable($envVar,'Machine')
if ($machineVal) { Set-Item -Path "Env:$envVar" -Value $machineVal }
Set-Location $root
$dll = Join-Path $root 'src\Bmt.Preflight\bin\Release\net8.0\preflight.dll'
$cfg = 'config/production/full-workload-open-loop-2host.json'
Write-Output ("=== preflight host=$env:COMPUTERNAME target=$Target root=$root env $envVar present=$([bool]$machineVal) ===")
& $dotnet $dll --config $cfg --target $Target 2>&1 | ForEach-Object { Write-Output $_ }
Write-Output ("exit=$LASTEXITCODE")
