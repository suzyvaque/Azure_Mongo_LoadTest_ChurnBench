[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunArgs,   # e.g. "status" or "start-balancer" or "shard calc_input ReqId"
    [string]$Vm = 'vm-hpc-loadgen-az1-0',
    [string]$ResourceGroup = 'rg-db-test-hpc',
    [switch]$SkipDeploy
)
$ErrorActionPreference = 'Stop'
$here = 'C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench\scripts\ops\docdb-admin'
$progB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content (Join-Path $here 'Program.cs') -Raw)))
$projB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content (Join-Path $here 'docdb-admin.csproj') -Raw)))
$lines = @()
$lines += '$d=''C:\docdbadmin''; New-Item -ItemType Directory -Force -Path $d | Out-Null'
if (-not $SkipDeploy) {
    $lines += "[IO.File]::WriteAllBytes(`"`$d\Program.cs`", [Convert]::FromBase64String('$progB64'))"
    $lines += "[IO.File]::WriteAllBytes(`"`$d\docdb-admin.csproj`", [Convert]::FromBase64String('$projB64'))"
}
$lines += 'Set-Location $d'
$lines += '$env:DOTNET_CLI_TELEMETRY_OPTOUT=''1''; $env:DOTNET_NOLOGO=''1'''
$lines += ('& C:\dotnet\dotnet.exe run -c Release -- ' + $RunArgs + ' 2>&1 | Out-String')
$tmp = Join-Path $here '_deploy.ps1'
Set-Content -Path $tmp -Value ($lines -join "`r`n") -Encoding utf8
$r = az vm run-command invoke -g $ResourceGroup --name $Vm --command-id RunPowerShellScript --scripts "@$tmp" -o json 2>&1 | ConvertFrom-Json
Write-Host "--- [$Vm] docdb-admin $RunArgs ---"
Write-Host $r.value[0].message
if ($r.value[1].message) { Write-Host "[STDERR] $($r.value[1].message)" }
