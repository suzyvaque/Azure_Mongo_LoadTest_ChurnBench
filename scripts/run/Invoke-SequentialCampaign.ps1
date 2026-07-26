<#
.SYNOPSIS
  Item 3/4: sequential mongo-shard-then-documentdb campaign into ONE grouped run folder, then build the
  aggregated comparison summary. Wraps Invoke-Campaign.ps1 per target and Build-RunSummary.ps1 at the end.

.DESCRIPTION
  Produces the requested layout on each generator host:
      <RepoDir>\results\run-{yyyyMMdd}-{NN}\mongo\iter-NN\*.json
      <RepoDir>\results\run-{yyyyMMdd}-{NN}\docdb\iter-NN\*.json
  by running (in order):
      1. Invoke-Campaign -Target mongo-shard -CampaignName mongo -ResultsDir results/run-... -CleanOutputAfter
      2. Invoke-Campaign -Target documentdb  -CampaignName docdb -ResultsDir results/run-... -CleanOutputAfter
  Each per-target campaign runs the synchronized 3-host iteration loop, then empties calc_output (Item 2).

  COLLECTION + SUMMARY: the per-host artifacts live on each host under the grouped run folder. Gather them
  into a single directory (via -PushResults + Merge-Campaign, a file share, or manual copy) and pass that
  directory as -CollectDir, then this wrapper calls Build-RunSummary.ps1 to emit
  <CollectDir>\summary-run-...-documentdb-vs-mongo.md (with the SRV access-path disclosure, Item 5).
  If -CollectDir is omitted the wrapper stops after the campaigns and prints the collect/summary command.

.PARAMETER Iterations    Synchronized iterations per target. Default 3.
.PARAMETER Config        Config path passed to each host. Default the open-loop 3-host config.
.PARAMETER ResourceGroup RG holding the generator VMs. Default rg-db-test-hpc.
.PARAMETER RepoDir       Repo root on each host. Default C:\bmt.
.PARAMETER LeadSeconds   Lead for the FIRST iteration's shared start. Default 300 (allows 100k warm-up, Item 6/7).
.PARAMETER RunFolder     Override the run-folder name (default results/run-{yyyyMMdd}-{NN}, NN auto-incremented).
.PARAMETER Targets       Ordered targets. Default 'mongo-shard','documentdb'.
.PARAMETER CollectDir    Local directory holding the collected per-host run folder; when set, build the summary.
.PARAMETER PushResults   Pass through to each host (git-push results for later collection/merge).
.PARAMETER NoPreflight   Pass --no-preflight to each host (NOT recommended).

.EXAMPLE
  .\Invoke-SequentialCampaign.ps1 -Iterations 3 -PushResults
#>
[CmdletBinding()]
param(
    [int]$Iterations = 3,
    [string]$Config = 'config/production/full-workload-open-loop-3host.json',
    [string]$ResourceGroup = 'rg-db-test-hpc',
    [string]$RepoDir = 'C:\bmt',
    [int]$LeadSeconds = 300,
    [string]$RunFolder,
    [string[]]$Targets = @('mongo-shard', 'documentdb'),
    [string]$CollectDir,
    [switch]$PushResults,
    [switch]$NoPreflight
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---- Compute the grouped run-folder name: results/run-{yyyyMMdd}-{NN} (NN = next free on THIS box) ----
if (-not $RunFolder) {
    $date = Get-Date -Format 'yyyyMMdd'
    $localRoot = Join-Path $RepoDir 'results'
    $n = 1
    while (Test-Path (Join-Path $localRoot ("run-$date-{0:D2}" -f $n))) { $n++ }
    $RunFolder = "results/run-$date-{0:D2}" -f $n
}
# Per-target subfolder name (folder key used by --campaign-name and Build-RunSummary).
$subFor = { param($t) switch ($t) { 'mongo-shard' { 'mongo' } 'mongo-vm' { 'mongo' } 'documentdb' { 'docdb' } 'cosmos-ru' { 'cosmos' } default { $t } } }

Write-Host "==== Sequential campaign ====" -ForegroundColor Cyan
Write-Host "  run folder : $RunFolder  (on each host, under $RepoDir)"
Write-Host "  targets    : $($Targets -join ' -> ')"
Write-Host "  iterations : $Iterations   config: $Config   first-lead: ${LeadSeconds}s"
Write-Host "=============================" -ForegroundColor Cyan

$campaign = Join-Path $here 'Invoke-Campaign.ps1'
foreach ($t in $Targets) {
    $sub = & $subFor $t
    Write-Host ""
    Write-Host ">>> Target '$t' -> $RunFolder/$sub" -ForegroundColor Cyan
    $campaignArgs = @{
        Target = $t; Iterations = $Iterations; Config = $Config; ResourceGroup = $ResourceGroup
        RepoDir = $RepoDir; LeadSeconds = $LeadSeconds; CampaignName = $sub; ResultsDir = $RunFolder
        CleanOutputAfter = $true
    }
    if ($PushResults) { $campaignArgs.PushResults = $true }
    if ($NoPreflight) { $campaignArgs.NoPreflight = $true }
    & $campaign @campaignArgs
    if ($LASTEXITCODE -ne 0) { throw "Campaign for target '$t' failed (exit $LASTEXITCODE)." }
}

Write-Host ""
Write-Host "All targets complete. Grouped artifacts on each host: $RepoDir\$($RunFolder -replace '/','\')" -ForegroundColor Green

# ---- Build the aggregated summary if a collected directory was provided ----
if ($CollectDir) {
    if (-not (Test-Path $CollectDir)) { throw "CollectDir not found: $CollectDir" }
    Write-Host ""
    Write-Host "Building aggregated summary from $CollectDir ..." -ForegroundColor Cyan
    & (Join-Path $here 'Build-RunSummary.ps1') -RunFolder $CollectDir
} else {
    Write-Host ""
    Write-Host "Next: collect every host's '$RunFolder' into one directory (via -PushResults + Merge-Campaign," -ForegroundColor Yellow
    Write-Host "a file share, or manual copy), then run:" -ForegroundColor Yellow
    Write-Host "  .\Build-RunSummary.ps1 -RunFolder <collected-run-folder>" -ForegroundColor Yellow
}
