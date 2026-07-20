<#
.SYNOPSIS
  Run a full benchmark campaign on ONE machine (or one co-located VM set), SEQUENTIALLY across targets,
  then auto-generate the summary + cross-target comparison + HTML report — no manual "summarize this"
  step. This replaces the ad-hoc per-run summarization loop.

.DESCRIPTION
  For each target in -Targets, in order:
    1. preflight  (unless -SkipPreflight)  — the §6.3 gate; ABORTS the target if it fails.
    2. test       — the timed connection-churn run (scenario + knobs come from the config's run.json).
    3. TIME_WAIT drain wait before the next target so ephemeral ports/ sockets fully recycle.
  After ALL targets:
    4. report     — writes summary.md (per-target percentiles + churn verdict + CROSS-TARGET comparison)
                    and the self-contained HTML into the campaign folder.
    5. Azure metric pull (server-side capture) — GUARDED: no-ops cleanly when `az` is not logged in or
       config/azure-resources.json is unfilled, so this runner is fully usable without any Azure setup.
    6. Writes a consolidated run-log and an INDEX.md for the campaign.

  Targets run ONE AT A TIME (never in parallel) so a single VM set is never split across backends and the
  cross-AZ fairness holds. Connection strings are read from each target's machine env var, never passed on
  the command line: documentdb->BMT_CONN, mongo-vm->BMT_CONN_MONGO, mongo-shard->BMT_CONN_MONGO_SHARD,
  cosmos-ru->BMT_CONN_COSMOS.

.PARAMETER Targets       Ordered backend keys to run. Default: mongo-shard, documentdb.
.PARAMETER Config        Config path (its run.json supplies iterations/duration/rates/open-loop).
                         Default: config/production/full-workload.json.
.PARAMETER Scenario      steady | burst | both. Default: burst.
.PARAMETER RunTag        Campaign tag / folder name under results/. Default: local-<yyyyMMdd-HHmmss>.
.PARAMETER ResultsRoot   Root results directory. Default: results.
.PARAMETER RepoDir       Repo root. Default: two levels up from this script.
.PARAMETER DrainSeconds  Seconds to wait between targets for TIME_WAIT/port drain. Default 45.
.PARAMETER SkipPreflight Skip the preflight gate for every target (NOT recommended).
.PARAMETER AzureResources Path to the azure-resources.json identifiers file. Default: config/azure-resources.json.

.EXAMPLE
  # Default: burst campaign, mongo-shard then documentdb, auto summary + comparison:
  .\Invoke-LocalCampaign.ps1

.EXAMPLE
  # Steady single-find campaign across three targets with a custom tag:
  .\Invoke-LocalCampaign.ps1 -Targets mongo-shard,documentdb,cosmos-ru `
      -Config config/production/single-find.json -Scenario steady -RunTag find-steady-round7
#>
[CmdletBinding()]
param(
    [ValidateSet('documentdb','mongo-vm','mongo-shard','cosmos-ru')]
    [string[]]$Targets = @('mongo-shard','documentdb'),
    [string]$Config = 'config/production/full-workload.json',
    [ValidateSet('steady','burst','both')] [string]$Scenario = 'burst',
    [string]$RunTag,
    [string]$ResultsRoot = 'results',
    [string]$RepoDir,
    [int]$DrainSeconds = 45,
    [switch]$SkipPreflight,
    [string]$AzureResources = 'config/azure-resources.json'
)

$ErrorActionPreference = 'Stop'

# ---- Resolve repo root and move into it so all relative paths line up ----
if (-not $RepoDir) { $RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
Set-Location $RepoDir
if (-not $RunTag) { $RunTag = "local-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }

$campaignRoot = Join-Path $ResultsRoot $RunTag
New-Item -ItemType Directory -Force -Path $campaignRoot | Out-Null
$runLog = Join-Path $campaignRoot 'run-log.txt'

function Log {
    param([string]$Message, [string]$Color = 'Gray')
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line -ForegroundColor $Color
    Add-Content -Path $runLog -Value $line
}

function ConnEnvVar([string]$t) {
    switch ($t) {
        'documentdb'  { 'BMT_CONN' }
        'mongo-vm'    { 'BMT_CONN_MONGO' }
        'mongo-shard' { 'BMT_CONN_MONGO_SHARD' }
        'cosmos-ru'   { 'BMT_CONN_COSMOS' }
    }
}

# ---- Server-side Azure metric pull (Package B5 hook). GUARDED so it is a clean no-op when Azure is not
#      set up, keeping this runner fully usable in Package A without any az login. When enabled it records
#      the exact run window + resource identifiers; the detailed `az monitor` / serverStatus queries are
#      filled in by Package B (which runs on the AZ1 host set with az login + azure-resources.json filled). ----
function Invoke-AzureMetricPull {
    param(
        [string]$CampaignRoot,
        [string]$AzureResources,
        [datetimeoffset]$StartUtc,
        [datetimeoffset]$EndUtc,
        [string[]]$Targets
    )

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Log 'azure metrics: `az` CLI not found — skipping server-side capture (Package B fills this).' 'DarkGray'
        return
    }
    az account show 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        Log 'azure metrics: not logged in (`az login` required) — skipping server-side capture.' 'DarkGray'
        return
    }
    if (-not (Test-Path $AzureResources)) {
        Log "azure metrics: '$AzureResources' not found — skipping server-side capture." 'DarkGray'
        return
    }

    try {
        $res = Get-Content $AzureResources -Raw | ConvertFrom-Json
    } catch {
        Log "azure metrics: '$AzureResources' is not valid JSON — skipping." 'DarkGray'
        return
    }
    if ([string]::IsNullOrWhiteSpace($res.Subscription) -or [string]::IsNullOrWhiteSpace($res.ResourceGroup)) {
        Log 'azure metrics: azure-resources.json has empty Subscription/ResourceGroup — skipping (fill it in Package B).' 'DarkGray'
        return
    }

    # Enabled: record the window + resource identifiers so Package B's detailed pull is a drop-in.
    $out = [pscustomobject]@{
        runWindowStartUtc = $StartUtc.UtcDateTime.ToString('o')
        runWindowEndUtc   = $EndUtc.UtcDateTime.ToString('o')
        targets           = $Targets
        subscription      = $res.Subscription
        resourceGroup     = $res.ResourceGroup
        note              = 'Run window + resource ids captured. Detailed az monitor / serverStatus queries are performed by Package B5.'
    }
    $metricsPath = Join-Path $CampaignRoot 'azure-metrics.json'
    $out | ConvertTo-Json -Depth 6 | Set-Content -Path $metricsPath -Encoding utf8
    Log "azure metrics: run window + resource ids recorded -> $metricsPath" 'Cyan'
}

# ---- Campaign INDEX.md (human entry point for the run) ----
function Write-CampaignIndex {
    param(
        [string]$CampaignRoot,
        [string]$RunTag,
        [string]$Config,
        [string]$Scenario,
        [datetimeoffset]$StartUtc,
        [datetimeoffset]$EndUtc,
        [object[]]$Outcomes,
        [string]$SummaryMd,
        [string]$HtmlOut
    )

    $md = [System.Collections.Generic.List[string]]::new()
    $md.Add("# Campaign $RunTag")
    $md.Add('')
    $md.Add("- Started (UTC): $($StartUtc.UtcDateTime.ToString('o'))")
    $md.Add("- Finished (UTC): $($EndUtc.UtcDateTime.ToString('o'))")
    $md.Add("- Config: ``$Config``")
    $md.Add("- Scenario: $Scenario")
    $md.Add('')
    $md.Add('## Targets')
    $md.Add('')
    $md.Add('| # | Target | Status | Started (UTC) | Finished (UTC) |')
    $md.Add('|---|---|---|---|---|')
    for ($i = 0; $i -lt $Outcomes.Count; $i++) {
        $o = $Outcomes[$i]
        $md.Add("| $($i + 1) | $($o.Target) | $($o.Status) | $($o.StartUtc) | $($o.EndUtc) |")
    }
    $md.Add('')
    $md.Add('## Artifacts')
    $md.Add('')
    $md.Add("- Summary + cross-target comparison: [$(Split-Path $SummaryMd -Leaf)]($(Split-Path $SummaryMd -Leaf))")
    $md.Add("- HTML report: [$(Split-Path $HtmlOut -Leaf)]($(Split-Path $HtmlOut -Leaf))")
    $md.Add('- Consolidated run log: [run-log.txt](run-log.txt)')
    if (Test-Path (Join-Path $CampaignRoot 'azure-metrics.json')) {
        $md.Add('- Server-side Azure metrics: [azure-metrics.json](azure-metrics.json)')
    }
    $md.Add('- Per-target result folders (per-iteration JSON/CSV + aggregate.json) are the sibling directories here.')

    Set-Content -Path (Join-Path $CampaignRoot 'INDEX.md') -Value $md -Encoding utf8
}

Log "==== Local sequential campaign '$RunTag' ====" 'Cyan'
Log "repo         : $RepoDir"
Log "targets      : $($Targets -join ' -> ')"
Log "config       : $Config"
Log "scenario     : $Scenario"
Log "results dir  : $campaignRoot"
Log "drain (s)    : $DrainSeconds"
Log "preflight    : $(if ($SkipPreflight) {'SKIPPED'} else {'on'})"

# ---- Build once up front (Release) ----
Log 'building solution (Release)...' 'DarkGray'
dotnet build Bmt.sln -c Release --nologo -v q *>&1 | Tee-Object -FilePath $runLog -Append | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$campaignStartUtc = [DateTimeOffset]::UtcNow
$targetOutcomes = [System.Collections.Generic.List[object]]::new()

for ($ti = 0; $ti -lt $Targets.Count; $ti++) {
    $target = $Targets[$ti]
    Log ('-' * 70)
    Log "TARGET $($ti + 1)/$($Targets.Count): $target" 'Green'

    # ---- Confirm the connection env var is present (never print its value) ----
    $envVar = ConnEnvVar $target
    if (-not [Environment]::GetEnvironmentVariable($envVar)) {
        $machineVal = [Environment]::GetEnvironmentVariable($envVar, 'Machine')
        if ($machineVal) { Set-Item -Path "Env:$envVar" -Value $machineVal }
        else { throw "Connection env var '$envVar' for target '$target' is not set (user or machine scope)." }
    }
    Log "connection env '$envVar' present (value hidden)." 'DarkGray'

    $targetStartUtc = [DateTimeOffset]::UtcNow

    # ---- 1) Preflight gate ----
    if (-not $SkipPreflight) {
        Log "preflight $target ..." 'DarkGray'
        $pfJson = Join-Path $campaignRoot ("preflight-{0}-{1}.json" -f $target, (Get-Date -Format 'yyyyMMdd-HHmmss'))
        dotnet run --project src/Bmt.Preflight -c Release --no-build -- `
            preflight --config $Config --target $target --warmup --json $pfJson *>&1 |
            Tee-Object -FilePath $runLog -Append
        if ($LASTEXITCODE -ne 0) {
            Log "PREFLIGHT FAILED for $target (exit $LASTEXITCODE) — skipping this target." 'Red'
            $targetOutcomes.Add([pscustomobject]@{ Target = $target; Status = 'preflight-failed'; StartUtc = $targetStartUtc.UtcDateTime.ToString('o') })
            continue
        }
    }

    # ---- 2) Timed run ----
    Log "test $target ($Scenario) ..." 'Green'
    dotnet run --project src/Bmt.LoadGen -c Release --no-build -- `
        test --target $target --scenario $Scenario --config $Config `
        --results $campaignRoot --run-tag $RunTag *>&1 |
        Tee-Object -FilePath $runLog -Append
    $runExit = $LASTEXITCODE
    $targetEndUtc = [DateTimeOffset]::UtcNow
    if ($runExit -ne 0) {
        Log "RUN FAILED for $target (exit $runExit)." 'Red'
        $targetOutcomes.Add([pscustomobject]@{ Target = $target; Status = "run-failed($runExit)"; StartUtc = $targetStartUtc.UtcDateTime.ToString('o'); EndUtc = $targetEndUtc.UtcDateTime.ToString('o') })
    } else {
        Log "run complete for $target." 'Green'
        $targetOutcomes.Add([pscustomobject]@{ Target = $target; Status = 'ok'; StartUtc = $targetStartUtc.UtcDateTime.ToString('o'); EndUtc = $targetEndUtc.UtcDateTime.ToString('o') })
    }

    # ---- 3) TIME_WAIT / ephemeral-port drain before the next target ----
    if ($ti -lt $Targets.Count - 1 -and $DrainSeconds -gt 0) {
        Log "draining sockets for ${DrainSeconds}s before the next target..." 'DarkGray'
        Start-Sleep -Seconds $DrainSeconds
    }
}

$campaignEndUtc = [DateTimeOffset]::UtcNow

# ---- 4) Report: summary.md (+ cross-target comparison) + HTML ----
Log ('-' * 70)
Log 'generating report (summary.md + comparison + HTML)...' 'Cyan'
$htmlOut = Join-Path $campaignRoot 'summary.html'
dotnet run --project src/Bmt.Report -c Release --no-build -- `
    report --input $campaignRoot --output $htmlOut *>&1 |
    Tee-Object -FilePath $runLog -Append
$summaryMd = Join-Path $campaignRoot 'summary.md'

# ---- 5) Server-side Azure metric pull (GUARDED — no-ops without az / unfilled resources) ----
Invoke-AzureMetricPull -CampaignRoot $campaignRoot -AzureResources $AzureResources `
    -StartUtc $campaignStartUtc -EndUtc $campaignEndUtc -Targets $Targets

# ---- 6) INDEX.md ----
Write-CampaignIndex -CampaignRoot $campaignRoot -RunTag $RunTag -Config $Config -Scenario $Scenario `
    -StartUtc $campaignStartUtc -EndUtc $campaignEndUtc -Outcomes $targetOutcomes -SummaryMd $summaryMd -HtmlOut $htmlOut

Log ('-' * 70)
Log "campaign '$RunTag' complete." 'Green'
Log "  summary : $summaryMd"
Log "  html    : $htmlOut"
Log "  index   : $(Join-Path $campaignRoot 'INDEX.md')"
Log "  run-log : $runLog"
