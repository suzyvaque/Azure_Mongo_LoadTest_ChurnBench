#requires -Version 5
<#
  Bootstrap a freshly-deployed *-gen2 load-generator VM to match the existing hosts.
  Idempotent: safe to re-run. Runs under SYSTEM via `az vm run-command invoke`.
  Steps: (1) TCP churn tuning  (2) .NET 8 SDK  (3) Git  (4) clone+build repo branch.
  A separate `az vm restart` afterwards makes the TCP TIME_WAIT change fully effective.
#>
$ErrorActionPreference = 'Stop'
$RepoUrl    = 'https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench.git'
$RepoBranch = 'feat/multihost-burst'
$RepoDir    = 'C:\bmt'
$DotnetRoot = 'C:\dotnet'

function Log($m) { Write-Output ("[setup] {0}" -f $m) }

# --- STEP 1: TCP tuning (ephemeral 10000-65534, TcpTimedWaitDelay=30) -------
Log 'STEP 1: TCP tuning'
netsh int ipv4 set dynamicport tcp start=10000 num=55535 | Out-Null
$tcp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters'
New-ItemProperty -Path $tcp -Name 'TcpTimedWaitDelay' -PropertyType DWord -Value 30 -Force | Out-Null
New-ItemProperty -Path $tcp -Name 'MaxUserPort' -PropertyType DWord -Value 65534 -Force | Out-Null
Log ("  ephemeral=" + ((netsh int ipv4 show dynamicport tcp) -join ' '))

# --- STEP 2: .NET 8 SDK -----------------------------------------------------
Log 'STEP 2: .NET 8 SDK'
$dotnetExe = Join-Path $DotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnetExe)) {
    $inst = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $inst -UseBasicParsing
    & $inst -Channel 8.0 -Quality GA -InstallDir $DotnetRoot -NoPath
    Log '  .NET SDK installed'
} else {
    Log '  .NET already present'
}
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', $DotnetRoot, 'Machine')
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($machinePath -notlike "*$DotnetRoot*") {
    [Environment]::SetEnvironmentVariable('Path', "$machinePath;$DotnetRoot", 'Machine')
}
$env:DOTNET_ROOT = $DotnetRoot
$env:Path = "$env:Path;$DotnetRoot"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Log ("  dotnet version: " + (& $dotnetExe --version))

# --- STEP 3: Git ------------------------------------------------------------
Log 'STEP 3: Git'
$gitExe = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $gitExe)) {
    $rel = Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers @{ 'User-Agent' = 'bmt-setup' }
    $asset = $rel.assets | Where-Object { $_.name -match 'Git-.*-64-bit\.exe$' } | Select-Object -First 1
    $gitInst = Join-Path $env:TEMP 'git-setup.exe'
    Invoke-WebRequest $asset.browser_download_url -OutFile $gitInst -UseBasicParsing
    Start-Process $gitInst -ArgumentList '/VERYSILENT','/NORESTART','/SP-','/SUPPRESSMSGBOXES' -Wait
    Log '  Git installed'
} else {
    Log '  Git already present'
}
$env:Path = "$env:Path;C:\Program Files\Git\cmd"
Log ("  git version: " + (& $gitExe --version))

# --- STEP 4: clone + build --------------------------------------------------
Log 'STEP 4: clone + build'
if (-not (Test-Path (Join-Path $RepoDir 'Bmt.sln'))) {
    if (Test-Path $RepoDir) { Remove-Item $RepoDir -Recurse -Force }
    & $gitExe clone --branch $RepoBranch $RepoUrl $RepoDir
} else {
    & $gitExe -C $RepoDir fetch origin $RepoBranch
    & $gitExe -C $RepoDir checkout $RepoBranch
    & $gitExe -C $RepoDir reset --hard "origin/$RepoBranch"
}
Log ("  on branch: " + (& $gitExe -C $RepoDir rev-parse --abbrev-ref HEAD) + " @ " + (& $gitExe -C $RepoDir rev-parse --short HEAD))
& $dotnetExe build (Join-Path $RepoDir 'Bmt.sln') -c Release --verbosity minimal
Log ("  build exit code: $LASTEXITCODE")
Log 'DONE'
