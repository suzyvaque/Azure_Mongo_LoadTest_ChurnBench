# VM1-az2 Setup & DocumentDB Test Runbook
#
# Run this script on VM1-az2 (Windows Server 2025) as Administrator in PowerShell.
# It sets up the benchmark harness identically to VM1 and runs the DocumentDB Phase-1 tests.
#
# Sections:
#   STEP 0 – Prerequisites (verify before running)
#   STEP 1 – TCP tuning (must match VM1 exactly; requires reboot to take effect)
#   STEP 2 – Install .NET 8 SDK
#   STEP 3 – Clone the repo
#   STEP 4 – Set connection string
#   STEP 5 – Verify private network reachability
#   STEP 6 – Run tests (full-workload, 3 x 30 min)
#   STEP 7 – Commit and push results
#
# Copy this file to VM1-az2, then run each STEP block manually so you can inspect
# the output before proceeding to the next step.

# ---------------------------------------------------------------------------
# STEP 0 – PREREQUISITES  (verify manually, nothing to run)
# ---------------------------------------------------------------------------
# a) VM1-az2 is in the same VNet as (or has VNet peering to) the DocumentDB
#    private endpoint VNet.
# b) The DocumentDB private DNS zone is LINKED to VM1-az2's VNet so that the
#    mongodb+srv SRV hostname resolves to the private IP (not the public one).
#    Azure portal: Private DNS zones → <docdb-zone> → Virtual network links → Add.
# c) Windows Firewall outbound rules allow TCP 27017 (usually open by default).
# d) You are logged in as a local Administrator (for registry + netsh changes).

# ---------------------------------------------------------------------------
# STEP 1 – TCP TUNING  (must match VM1: ephemeral 10000-65534, TcpTimedWaitDelay=30)
# ---------------------------------------------------------------------------
# Matches VM1 settings exactly. Requires a reboot to take full effect.
# Run this block, then reboot, then continue from STEP 2.

# Expand ephemeral port range: start=10000, count=55535  (10000-65534, same as VM1)
netsh int ipv4 set dynamicport tcp start=10000 num=55535
netsh int ipv4 show dynamicport tcp   # verify: Start=10000, Number=55535

# Reduce TIME_WAIT from default 240 s to 30 s (same as VM1)
Set-ItemProperty `
    -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters" `
    -Name "TcpTimedWaitDelay" -Value 30 -Type DWord

# Confirm
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters").TcpTimedWaitDelay

Write-Host "TCP tuning applied. Reboot now, then continue from STEP 2." -ForegroundColor Yellow
# Restart-Computer -Force   # uncomment to reboot immediately

# ---------------------------------------------------------------------------
# STEP 2 – INSTALL .NET 8 SDK  (version 8.0.x, same major as VM1: 8.0.422)
# ---------------------------------------------------------------------------
# Option A: winget (simplest — works on Win2025 with the App Installer package)
winget install --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements

# Option B: direct download if winget is not available
# $msi = "$env:TEMP\dotnet-sdk-8.msi"
# Invoke-WebRequest "https://download.visualstudio.microsoft.com/download/pr/dotnet-sdk-8.0.422-win-x64.exe" -OutFile "$env:TEMP\dotnet-sdk-8.exe"
# Start-Process "$env:TEMP\dotnet-sdk-8.exe" -ArgumentList "/install /quiet /norestart" -Wait

# Verify
dotnet --version   # must show 8.x.xxx

# ---------------------------------------------------------------------------
# STEP 3 – CLONE THE REPO
# ---------------------------------------------------------------------------
# Adjust the target directory if you want a different location.
$RepoDir = "C:\bmt"
git clone https://github.com/suzyvaque/Azure_Mongo_LoadTest_ChurnBench $RepoDir
Set-Location $RepoDir

# Set your git identity so commits have a proper author
git config user.name  "vm1-az2"
git config user.email "vm1-az2@benchmarks.local"

# Restore NuGet packages (downloads ~30 MB; needs internet)
dotnet restore Bmt.sln

# Build release binary once (faster than `dotnet run` with JIT on first run)
dotnet build Bmt.sln -c Release --no-restore -q
Write-Host "Build result: $LASTEXITCODE  (must be 0)" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# STEP 4 – SET CONNECTION STRING
# ---------------------------------------------------------------------------
# The DocumentDB connection string uses the same credential as on VM1.
# Copy the value of BMT_CONN from VM1 (it is already set there as an env var).
# URI form (mongodb+srv, TLS, SCRAM-SHA-256, retrywrites=false):
#
#   mongodb+srv://<user>:<pass>@docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com/
#     ?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000
#
# IMPORTANT: Once DNS resolves inside this VNet (after STEP 0b), the hostname
#   docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com
# will resolve to the private endpoint IP 10.2.0.7 — NOT a public IP.
# The SRV target is fc-d3df9fb90605-000.global.mongocluster.cosmos.azure.com on port 10260.
# Make sure that hostname also resolves to a private IP (same DNS zone covers it).

$env:BMT_CONN = "REPLACE_WITH_BMT_CONN_VALUE_FROM_VM1"   # copy from VM1: $env:BMT_CONN

# (Optional) persist across reboots / new sessions:
# [System.Environment]::SetEnvironmentVariable("BMT_CONN", $env:BMT_CONN, "Machine")

# Mask-check — should print mongodb+srv://****:****@docdb-dbtest-hpc-0...
$env:BMT_CONN -replace '//[^:]+:[^@]+@', '//****:****@'

# ---------------------------------------------------------------------------
# STEP 5 – VERIFY PRIVATE NETWORK REACHABILITY
# ---------------------------------------------------------------------------
# Private endpoint for DocumentDB vCore: IP 10.2.0.7, lives in vm-dbtest-hpc-0-vnet.
# The private DNS zone "privatelink.mongocluster.cosmos.azure.com" is currently linked
# ONLY to vm-dbtest-hpc-0-vnet (VM1's VNet).
#
# Before this step will succeed you MUST (STEP 0b):
#   Azure portal → Private DNS zones → privatelink.mongocluster.cosmos.azure.com
#   → Virtual network links → + Add → link to VM1-az2's VNet
#   (same for privatelink.mongo.cosmos.azure.com if it also has records for this cluster)
#
# Once the DNS link is in place AND VNet peering routes 10.2.0.7 to VM1's VNet,
# both the hostname and TCP test below should succeed.

$DocDbHost = "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com"
$DocDbPrivateIP = "10.2.0.7"   # DocumentDB private endpoint IP (confirmed on VM1)
$DocDbPort = 27017

# DNS resolution check (should resolve to 10.2.0.7 when DNS zone is linked)
Write-Host "=== DNS resolution ==="
try {
    Resolve-DnsName $DocDbHost -ErrorAction Stop | Format-Table Name,Type,IPAddress,NameHost -AutoSize
} catch {
    Write-Host "DNS resolution FAILED for $DocDbHost — add DNS zone link first (STEP 0b)" -ForegroundColor Red
}

# TCP check via private IP (bypasses DNS — useful to test routing independently)
Write-Host "=== TCP via private IP (10.2.0.7:27017) ==="
$r = Test-NetConnection -ComputerName $DocDbPrivateIP -Port $DocDbPort -WarningAction SilentlyContinue
if ($r.TcpTestSucceeded) {
    Write-Host "TCP reachability via IP: OK" -ForegroundColor Green
} else {
    Write-Host "TCP reachability via IP: FAILED. Check VNet peering and NSG rules." -ForegroundColor Red
    Write-Host "Do NOT proceed to STEP 6 until this is green." -ForegroundColor Red
}

# TCP check via hostname (requires DNS zone link)
Write-Host "=== TCP via hostname ==="
$r2 = Test-NetConnection -ComputerName $DocDbHost -Port $DocDbPort -WarningAction SilentlyContinue
if ($r2.TcpTestSucceeded) {
    Write-Host "TCP reachability via hostname: OK  (resolved to $($r2.RemoteAddress))" -ForegroundColor Green
} else {
    Write-Host "TCP reachability via hostname: FAILED. DNS zone link may be missing." -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# STEP 6 – RUN DOCUMENTDB TESTS  (full-workload, 3 x 30 min = 90 min total)
# ---------------------------------------------------------------------------
# Make sure you are in the repo directory and BMT_CONN is set before running.
Set-Location $RepoDir

# The production config runs:
#   Scenario: steady + burst (--scenario both)
#   Iterations: 3
#   IterationDurationSeconds: 1800  (30 min each)
#   Workload: full-workload (4-op cycle)
#
# Preflight runs automatically and will abort if any check FAILS.
# To skip preflight during a connectivity test: add --no-preflight

dotnet run --project src/Bmt.LoadGen -c Release -- `
    test `
    --target documentdb `
    --scenario both `
    --config config/production/full-workload.json `
    --results results

# The above takes ~90 min. Artifacts land in:
#   results/documentdb-steady-burst-full-workload-<stamp>/
#     iter-01/  <runid>.json  + -timeseries.csv + -latency.csv
#     iter-02/  ...
#     iter-03/  ...
#     aggregate.json

# ---------------------------------------------------------------------------
# STEP 6b – CLEAN calc_output AFTER THE CAMPAIGN
# ---------------------------------------------------------------------------
# Empty only calc_output (keeps calc_input + ReqId index). REQUIRED after a
# single-insert run, which accumulates docs without bound; harmless after a
# 4-op or find-only run.
dotnet run --project src/Bmt.Seeder -c Release -- `
    clean-output `
    --target documentdb `
    --config config/production/full-workload.json

# ---------------------------------------------------------------------------
# STEP 7 – COMMIT AND PUSH RESULTS
# ---------------------------------------------------------------------------
Set-Location $RepoDir

git add results/
git status   # should show new documentdb-... folders

git commit -m "results: documentdb phase-1 3x30min full-workload $(Get-Date -Format 'yyyy-MM-dd')"

# Pull first to rebase on top of any VM1 results pushed concurrently
git pull --rebase origin main
git push origin main

Write-Host "Done. Results are now in the shared repo." -ForegroundColor Green

# ---------------------------------------------------------------------------
# NOTES
# ---------------------------------------------------------------------------
# Single-op smoke tests (run before the 90-min production run to verify config):
#
#   dotnet run --project src/Bmt.LoadGen -c Release -- `
#       test --target documentdb --scenario steady `
#       --config config/smoke/single-find.json --results results --no-preflight
#
#   dotnet run --project src/Bmt.LoadGen -c Release -- `
#       test --target documentdb --scenario steady `
#       --config config/smoke/full-workload.json --results results --no-preflight
#
# Production single-op runs (find-only / insert-only, 3 x 30 min each):
#   --config config/production/single-find.json
#   --config config/production/single-insert.json   (note: calc_output accumulates —
#       run `clean-output` before AND after; see STEP 6b)
#
# Cosmos RU throughput is 100,000 RU/s (set on 2026-06-18 from VM1; no action needed here).
#
# MongoDB VM replica set (active AZ3, standby AZ1) is running; BMT_CONN_MONGO connects
# to the active node directly. Not needed for DocumentDB tests but recorded for reference.
