<#
.SYNOPSIS
    Show or change the SHARED database-level provisioned throughput (RU/s) on the Cosmos DB for
    MongoDB (RU) backend used by this benchmark — for cost control between test rounds.

.DESCRIPTION
    The benchmark's `cosmos-ru` target is Azure Cosmos DB for MongoDB (RU model). Throughput is
    provisioned (manual) and SHARED at the DATABASE level on `bmt_db` — both `calc_input` and
    `calc_output` draw from the same RU/s budget; neither collection has dedicated throughput.

    100,000 RU/s is expensive to leave running idle. This script lets you:
      * raise to the test value (e.g. 100000) shortly BEFORE a Cosmos run, and
      * drop to the MINIMUM after the run (with an hour's buffer) to save cost.

    IMPORTANT — you cannot go to 0 RU/s on the provisioned model. Azure's floor is
        min RU/s = MAX(400, highest-ever-provisioned / 100, storage-based-floor)
    Because this database has been provisioned at 100,000 RU/s, the floor is ~1,000 RU/s and stays
    there permanently. -Min reads the REAL current minimum from Azure (does not guess) and sets it.
    To reach $0 you would have to DELETE the database/collections and re-seed 100k + reindex before
    the next run — not what this script does (it preserves the byte-identical seed-42 dataset).

    Every action is appended to scripts\cosmos-ru.log for an audit trail (timestamp, old -> new RU/s,
    who/where). The log is git-ignored (see .gitignore) because it is operational, not a result.

.PARAMETER Show
    Print the current throughput, the current Azure-enforced minimum, and whether a scale operation
    is still pending. Makes no change.

.PARAMETER Set
    Set the shared database throughput to this exact manual RU/s value (e.g. 100000). Must be >= the
    current minimum and a multiple of 100.

.PARAMETER Min
    Set the throughput to the current Azure-enforced MINIMUM (read live, typically 1000 RU/s here).
    Use this after a Cosmos run to minimise idle cost while keeping the data + indexes intact.

.PARAMETER Account
    Cosmos account name. Default: cosmos-dbtest-hpc-0

.PARAMETER ResourceGroup
    Resource group. Default: rg-db-test-hpc

.PARAMETER Database
    Mongo database holding the shared throughput. Default: bmt_db

.PARAMETER Wait
    After a change, poll until Azure reports the scale operation complete (offerReplacePending = false)
    or the throughput equals the requested value. Recommended before starting a timed run so you don't
    eat 429s while the scale-up is still in progress.

.EXAMPLE
    # Before a Cosmos run (raise to the test value and wait until it's live):
    pwsh -File scripts\cosmos-ru.ps1 -Set 100000 -Wait

.EXAMPLE
    # After the Cosmos run + buffer (drop to the real minimum to save cost):
    pwsh -File scripts\cosmos-ru.ps1 -Min

.EXAMPLE
    # Just inspect, change nothing:
    pwsh -File scripts\cosmos-ru.ps1 -Show
#>
[CmdletBinding(DefaultParameterSetName = 'Show')]
param(
    [Parameter(ParameterSetName = 'Show')]
    [switch]$Show,

    [Parameter(ParameterSetName = 'Set', Mandatory = $true)]
    [int]$Set,

    [Parameter(ParameterSetName = 'Min', Mandatory = $true)]
    [switch]$Min,

    [string]$Account = 'cosmos-dbtest-hpc-0',
    [string]$ResourceGroup = 'rg-db-test-hpc',
    [string]$Database = 'bmt_db',
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
$logFile = Join-Path $PSScriptRoot 'cosmos-ru.log'

function Write-Audit([string]$message) {
    $line = "{0}  {1}@{2}  {3}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $env:USERNAME, $env:COMPUTERNAME, $message
    Add-Content -Path $logFile -Value $line
    Write-Host $line
}

function Get-Throughput {
    $json = az cosmosdb mongodb database throughput show `
        --account-name $Account --resource-group $ResourceGroup --name $Database -o json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az throughput show failed: $json" }
    $o = $json | ConvertFrom-Json
    [PSCustomObject]@{
        Current  = [int]$o.resource.throughput
        Minimum  = [int]$o.resource.minimumThroughput
        InstantMax = [int]$o.resource.instantMaximumThroughput
        Pending  = [bool]$o.resource.offerReplacePending
    }
}

# --- Verify Azure context up front ---
$ctx = az account show --query "name" -o tsv 2>&1
if ($LASTEXITCODE -ne 0) { throw "Not logged in to Azure CLI. Run 'az login' first.`n$ctx" }
Write-Host "Azure subscription : $ctx"
Write-Host "Target             : $Account / $ResourceGroup / db=$Database (SHARED throughput)"

$state = Get-Throughput
Write-Host ("Current RU/s       : {0}   (Azure minimum {1}, instant-max {2}{3})" -f `
    $state.Current, $state.Minimum, $state.InstantMax, $(if ($state.Pending) { ', scale PENDING' } else { '' }))

# --- SHOW ---
if ($PSCmdlet.ParameterSetName -eq 'Show') {
    if ($state.Pending) { Write-Host 'A scale operation is still in progress.' -ForegroundColor Yellow }
    return
}

# --- Decide target value ---
if ($PSCmdlet.ParameterSetName -eq 'Min') {
    $target = $state.Minimum
    Write-Host "Requested          : MINIMUM => $target RU/s"
} else {
    $target = $Set
    Write-Host "Requested          : $target RU/s"
    if ($target % 100 -ne 0) { throw "RU/s must be a multiple of 100 (got $target)." }
    if ($target -lt $state.Minimum) {
        throw "Requested $target RU/s is below the Azure-enforced minimum $($state.Minimum) RU/s. " +
              "You cannot go lower without deleting the database (which loses the seeded dataset)."
    }
}

if ($target -eq $state.Current -and -not $state.Pending) {
    Write-Audit "no-op: already at $target RU/s"
    return
}

# --- Apply ---
Write-Audit "scaling $($state.Current) -> $target RU/s (requested)"
$upd = az cosmosdb mongodb database throughput update `
    --account-name $Account --resource-group $ResourceGroup --name $Database `
    --throughput $target -o none 2>&1
if ($LASTEXITCODE -ne 0) { Write-Audit "FAILED: $upd"; throw "az throughput update failed: $upd" }

# --- Optionally wait for completion ---
if ($Wait) {
    Write-Host 'Waiting for the scale operation to complete...' -NoNewline
    $deadline = (Get-Date).AddMinutes(20)
    do {
        Start-Sleep -Seconds 10
        Write-Host '.' -NoNewline
        $now = Get-Throughput
    } while (($now.Pending -or $now.Current -ne $target) -and (Get-Date) -lt $deadline)
    Write-Host ''
    if ($now.Current -eq $target -and -not $now.Pending) {
        Write-Audit "scale COMPLETE: now $($now.Current) RU/s"
    } else {
        Write-Audit "scale still pending after timeout: current $($now.Current) RU/s, pending=$($now.Pending)"
        Write-Host 'Re-run with -Show to confirm before starting a timed run.' -ForegroundColor Yellow
    }
} else {
    Write-Host 'Scale submitted. Re-run with -Show (or pass -Wait) to confirm it is live before a timed run.' -ForegroundColor Yellow
}
