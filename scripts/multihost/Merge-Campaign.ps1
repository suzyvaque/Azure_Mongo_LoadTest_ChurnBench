<#
.SYNOPSIS
  Aggregates a coordinated multi-host burst campaign's per-host artifacts into combined per-second
  conn/s + concurrency and reports whether the ≥1,200 conn/s / ≥11,000 concurrent envelope was
  actually reached (test_instruction.md §6.2 Track C).

.DESCRIPTION
  Thin wrapper over `Bmt.Report merge`. Point -InputDir at a directory that contains EVERY host's
  results/ (e.g. after each host pushed to the shared repo and you pulled, or after copying all hosts'
  results into one folder). Filters to one campaign by -RunTag, unions each host's per-second series on
  the absolute wall-clock second, and prints the combined conn/s + in-flight peaks vs the targets. Also
  writes merge.json + a combined per-second CSV per (target, scenario).

.PARAMETER RunTag          Campaign tag to merge (matches the hosts' --run-tag).
.PARAMETER InputDir        Directory containing all hosts' result JSONs (searched recursively). Default: results.
.PARAMETER Output          Merge-summary JSON path. Default: results\merge-<RunTag>.json
.PARAMETER ConcurrentTarget Combined concurrent target. Default 11000.
.PARAMETER ChurnTarget      Combined conn/s target. Default 1200.
.PARAMETER RepoDir          Repo root. Default: current directory.

.EXAMPLE
  .\Merge-Campaign.ps1 -RunTag docdb-m80-burst -InputDir results
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RunTag,
    [string]$InputDir = 'results',
    [string]$Output,
    [int]$ConcurrentTarget = 11000,
    [int]$ChurnTarget = 1200,
    [string]$RepoDir = '.'
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoDir

if (-not $Output) { $Output = Join-Path 'results' "merge-$RunTag.json" }

Write-Host "Merging campaign '$RunTag' from '$InputDir'..." -ForegroundColor Cyan
dotnet run --project src/Bmt.Report -c Release -- `
    merge `
    --input $InputDir `
    --tag $RunTag `
    --conc-target $ConcurrentTarget `
    --churn-target $ChurnTarget `
    --output $Output
if ($LASTEXITCODE -ne 0) { throw "merge failed (exit $LASTEXITCODE)." }

Write-Host "Merge summary: $Output" -ForegroundColor Green
Write-Host "Combined per-second CSV(s) written next to it (…-combined.csv)." -ForegroundColor Green
