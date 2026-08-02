$env:DOTNET_ROOT = 'C:\dotnet'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine')
$machineVal = [Environment]::GetEnvironmentVariable('BMT_CONN','Machine')
if ($machineVal) { $env:BMT_CONN = $machineVal }
Set-Location 'C:\bmt'
$dll = 'C:\bmt\src\Bmt.Seeder\bin\Release\net8.0\seeder.dll'
$cfg = 'config/production/full-workload-open-loop-3host.json'
Write-Output ("=== clean-output documentdb start=$(Get-Date -Format o) ===")
& 'C:\dotnet\dotnet.exe' $dll clean-output --target documentdb --config $cfg 2>&1 | ForEach-Object { Write-Output $_ }
Write-Output ("exit=$LASTEXITCODE end=$(Get-Date -Format o)")
