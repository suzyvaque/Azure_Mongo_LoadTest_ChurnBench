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
# Set the DocumentDB connection string for this session.
# Replace the placeholder below with the real value (get it from the shared
# key vault or from whoever manages the DocumentDB credentials).
#
# The connection string MUST contain:
#   retrywrites=false   (DocumentDB does not support retryable writes)
#   authSource=admin    (or whichever auth DB the user lives in)
#   NO SRV if the SRV record does not resolve from this VNet — use explicit host:port instead.
#
# Example form:
#   mongodb://user:pass@<docdb-private-endpoint>:27017/bmt_db?replicaSet=rs0&authSource=admin&retrywrites=false

$env:BMT_CONN = "REPLACE_WITH_DOCUMENTDB_CONNECTION_STRING"

# (Optional) persist across reboots / new sessions:
# [System.Environment]::SetEnvironmentVariable("BMT_CONN", $env:BMT_CONN, "Machine")

# Mask-check — should print mongodb://****:****@...
$env:BMT_CONN -replace '//[^:]+:[^@]+@', '//****:****@'

# ---------------------------------------------------------------------------
# STEP 5 – VERIFY PRIVATE NETWORK REACHABILITY
# ---------------------------------------------------------------------------
# Extract the hostname from the connection string (edit if your format differs).
# This checks TCP-level reachability before spending 90 min on a broken run.

# Replace with the actual DocumentDB private endpoint hostname or IP:
$DocDbHost = "REPLACE_WITH_DOCUMENTDB_PRIVATE_HOSTNAME_OR_IP"
$DocDbPort = 27017

$result = Test-NetConnection -ComputerName $DocDbHost -Port $DocDbPort
if ($result.TcpTestSucceeded) {
    Write-Host "TCP reachability: OK  ($DocDbHost`:$DocDbPort)" -ForegroundColor Green
} else {
    Write-Host "TCP reachability: FAILED. Check VNet peering, NSG rules, and private DNS zone link." -ForegroundColor Red
    Write-Host "Do NOT proceed to STEP 6 until this is green." -ForegroundColor Red
}

# Also verify SRV / DNS resolution (important for mongodb+srv:// URIs):
# Resolve-DnsName _mongodb._tcp.<your-docdb-cluster-hostname>

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
    --config config/config.json `
    --results results

# The above takes ~90 min. Artifacts land in:
#   results/documentdb-steady-burst-full-workload-<stamp>/
#     iter-01/  <runid>.json  + -timeseries.csv + -latency.csv
#     iter-02/  ...
#     iter-03/  ...
#     aggregate.json

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
#       --config config/smoke-single-find.json --results results --no-preflight
#
#   dotnet run --project src/Bmt.LoadGen -c Release -- `
#       test --target documentdb --scenario steady `
#       --config config/smoke-full.json --results results --no-preflight
#
# Cosmos RU throughput is 100,000 RU/s (set on 2026-06-18 from VM1; no action needed here).
#
# MongoDB VM replica set (active AZ3, standby AZ1) is running; BMT_CONN_MONGO connects
# to the active node directly. Not needed for DocumentDB tests but recorded for reference.
