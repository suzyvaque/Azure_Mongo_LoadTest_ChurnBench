<#
.SYNOPSIS
  Orchestrates a coordinated multi-host open-loop burst campaign across all co-located generator VMs
  for one target, from an operator box with Azure CLI (test_instruction.md §6.2 Track C).

.DESCRIPTION
  Picks the generator pool for the target (same-AZ VMs), computes a single shared --start-at UTC instant
  (now + LeadSeconds), then fires Run-BurstHost.ps1 on every host CONCURRENTLY via `az vm run-command
  invoke`, passing an incrementing --host-id, the shared --host-count, --run-tag and --start-at. Because
  all hosts share the same start instant, their bursts align and the combined conn/s + concurrency sum
  in the same wall-clock second even though the invocations don't fire at the exact same millisecond.

  Generator pools (deduced from the deployed topology; override with -HostVms):
    documentdb  -> AZ 2: vm-dbtest-hpc-0-az2, vm-dbtest-hpc-0-az2-gen2
    mongo-vm    -> AZ 3: vm-dbtest-hpc-0,     vm-dbtest-hpc-0-gen2
    mongo-shard -> AZ 3: vm-dbtest-hpc-0,     vm-dbtest-hpc-0-gen2

  Connection strings are NOT passed here — each host reads its own machine env var (set once per host,
  see runbook STEP 4), keeping secrets out of run-command logs.

  After all hosts finish (and -PushResults on each host pushed to the shared repo), run Merge-Campaign.ps1
  to prove the ≥1,200 conn/s / ≥11,000 concurrent envelope was reached.

.PARAMETER Target        documentdb | mongo-vm | mongo-shard | cosmos-ru.
.PARAMETER RunTag        Shared campaign tag. Default: <target>-<yyyyMMdd-HHmmss>.
.PARAMETER LeadSeconds   Seconds from now until the shared timed-phase start. Default 120 (allow build + preflight).
.PARAMETER ResourceGroup RG holding the generator VMs. Default: rg-db-test-hpc.
.PARAMETER HostVms       Explicit ordered VM-name list (overrides the deduced pool). Host-id = position.
.PARAMETER Config        Config path passed to each host.
.PARAMETER Scenario      steady | burst | both. Default burst.
.PARAMETER RepoDir       Repo root on each host. Default C:\bmt.
.PARAMETER PushResults   Tell each host to git-push its results (recommended for later merge).
.PARAMETER NoPreflight   Pass --no-preflight to each host (NOT recommended).

.EXAMPLE
  # 2-host DocumentDB burst starting 2 minutes from now:
  .\Invoke-Campaign.ps1 -Target documentdb -RunTag docdb-m80-burst -PushResults
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('documentdb','mongo-vm','mongo-shard','cosmos-ru')]
    [string]$Target,
    [string]$RunTag,
    [int]$LeadSeconds = 120,
    [string]$ResourceGroup = 'rg-db-test-hpc',
    [string[]]$HostVms,
    [string]$Config = 'config/production/full-workload-open-loop-multihost.json',
    [ValidateSet('steady','burst','both')] [string]$Scenario = 'burst',
    [string]$RepoDir = 'C:\bmt',
    [switch]$PushResults,
    [switch]$NoPreflight
)

$ErrorActionPreference = 'Stop'

# ---- Resolve the same-AZ generator pool for this target ----
if (-not $HostVms -or $HostVms.Count -eq 0) {
    $HostVms = switch ($Target) {
        'documentdb'  { @('vm-dbtest-hpc-0-az2', 'vm-dbtest-hpc-0-az2-gen2') }  # AZ 2
        'mongo-vm'    { @('vm-dbtest-hpc-0',     'vm-dbtest-hpc-0-gen2') }       # AZ 3
        'mongo-shard' { @('vm-dbtest-hpc-0',     'vm-dbtest-hpc-0-gen2') }       # AZ 3
        'cosmos-ru'   { @('vm-dbtest-hpc-0-az2', 'vm-dbtest-hpc-0-az2-gen2') }   # co-located w/ docdb AZ
    }
}
$hostCount = $HostVms.Count
if (-not $RunTag) { $RunTag = "$Target-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }

# ---- Single shared start instant for every host ----
$startAt = [DateTimeOffset]::UtcNow.AddSeconds($LeadSeconds).ToString('yyyy-MM-ddTHH:mm:ssZ')

Write-Host "==== Multi-host burst campaign ====" -ForegroundColor Cyan
Write-Host "  target     : $Target"
Write-Host "  run-tag    : $RunTag"
Write-Host "  host-count : $hostCount"
Write-Host "  hosts      : $($HostVms -join ', ')"
Write-Host "  start-at   : $startAt  (T+${LeadSeconds}s)"
Write-Host "  config     : $Config"
Write-Host "===================================" -ForegroundColor Cyan

$scriptPath = "$RepoDir\scripts\multihost\Run-BurstHost.ps1"
$pushFlag   = if ($PushResults) { '-PushResults' } else { '' }
$noPfFlag   = if ($NoPreflight) { '-NoPreflight' } else { '' }

# ---- Fire each host concurrently via az vm run-command (each as a background job) ----
$jobs = @()
for ($i = 0; $i -lt $hostCount; $i++) {
    $vm     = $HostVms[$i]
    $hostId = $i + 1

    # The inline script run ON the host: dot-invokes Run-BurstHost.ps1 with this host's parameters.
    $remote = @"
& '$scriptPath' -Target '$Target' -HostId $hostId -HostCount $hostCount ``
    -RunTag '$RunTag' -StartAtUtc '$startAt' -Config '$Config' -Scenario '$Scenario' ``
    -RepoDir '$RepoDir' $pushFlag $noPfFlag
"@

    Write-Host "[launch] host $hostId/$hostCount -> $vm" -ForegroundColor Green
    $jobs += Start-Job -Name "burst-$vm" -ScriptBlock {
        param($rg, $vmName, $remoteScript)
        az vm run-command invoke `
            --resource-group $rg `
            --name $vmName `
            --command-id RunPowerShellScript `
            --scripts $remoteScript `
            --output json 2>&1
    } -ArgumentList $ResourceGroup, $vm, $remote
}

Write-Host "All $hostCount hosts launched. Waiting for run-command completion (may take 15-25 min)..." -ForegroundColor Cyan
$jobs | Wait-Job | Out-Null

foreach ($j in $jobs) {
    Write-Host "---- $($j.Name) ----" -ForegroundColor Yellow
    Receive-Job $j
    Remove-Job $j
}

Write-Host ""
Write-Host "Campaign '$RunTag' dispatched to all hosts." -ForegroundColor Green
Write-Host "Next: once each host has pushed its results/, run:" -ForegroundColor Green
Write-Host "  .\Merge-Campaign.ps1 -RunTag $RunTag -InputDir <results-dir-with-all-hosts>" -ForegroundColor Green
