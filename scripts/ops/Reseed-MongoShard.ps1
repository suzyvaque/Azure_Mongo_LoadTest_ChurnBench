<#
.SYNOPSIS
  Reseed the sharded MongoDB cluster (mongo-shard target) to a clean 100,000 logical docs via mongos.
  Uses --force so calc_input/calc_output are emptied (batched deletes, sharding preserved) then reseeded
  from scratch (seed 42, byte-identical). Injects the Machine-scope BMT_CONN_MONGO_SHARD into the process
  (run-command's agent env may predate the var). Run on a generator VM that has the repo built at C:\bmt.
    az vm run-command invoke ... --scripts @scripts/ops/Reseed-MongoShard.ps1
#>
$env:DOTNET_ROOT = 'C:\dotnet'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine')
$machineVal = [Environment]::GetEnvironmentVariable('BMT_CONN_MONGO_SHARD','Machine')
if ($machineVal) { $env:BMT_CONN_MONGO_SHARD = $machineVal }
Set-Location 'C:\bmt'
$dll = 'C:\bmt\src\Bmt.Seeder\bin\Release\net8.0\seeder.dll'
$cfg = 'config/production/full-workload-open-loop-multihost.json'
Write-Output ("=== reseed mongo-shard --force (env present=$([bool]$machineVal)) start=$(Get-Date -Format o) ===")
& 'C:\dotnet\dotnet.exe' $dll prepare-data --target mongo-shard --config $cfg --force 2>&1 | ForEach-Object { Write-Output $_ }
Write-Output ("exit=$LASTEXITCODE end=$(Get-Date -Format o)")
