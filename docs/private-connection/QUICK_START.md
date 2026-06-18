# Private Connection Setup — Executive Summary

**Date:** 2026-06-18  
**VM:** vm-dbtest-hpc-0-az2 (this machine, in az2)  
**Target:** Azure DocumentDB at docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com  
**Status:** ✅ **Ready to Configure** (all resource discovery complete)

---

## 🎯 What You Need to Do

### Option A: Automated Setup (Recommended) — 2 minutes ✅

```powershell
# This will automatically configure everything
cd C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench.worktrees\agents-azure-documentdb-vm-connection\scripts
.\quick-setup-private-connection.ps1
```

**What it does:**
1. ✅ Creates VNet peering between vm-dbtest-hpc-0-az2-vnet ↔ vm-dbtest-hpc-0-vnet
2. ✅ Links Private DNS zones to this VM's VNet
3. ✅ Verifies all configuration is correct

### Option B: Manual Setup

Follow the instructions in `AZURE_DISCOVERY_REPORT.md` (Step 1 & 2 sections) using Azure CLI commands directly.

---

## 📋 Azure Resources Discovered

| Resource | Value |
|----------|-------|
| **Subscription** | ME-MngEnvMCAP379524-suzyvaque-1 |
| **Resource Group** | rg-db-test-hpc |
| **Region** | koreacentral |
| **DocumentDB Instance** | docdb-dbtest-hpc-0 |
| **DocumentDB Hostname** | docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com |
| **Private Endpoint IP** | 10.2.0.7 |
| **Private Endpoint Port** | 27017 |
| **DocDB VNet** | vm-dbtest-hpc-0-vnet (10.2.0.0/24) |
| **This VM's VNet** | vm-dbtest-hpc-0-az2-vnet (10.4.0.0/24) |
| **Private DNS Zones** | privatelink.mongocluster.cosmos.azure.com, privatelink.mongo.cosmos.azure.com |

---

## 🔧 Current Setup Status

| Component | Status | Action |
|-----------|--------|--------|
| DocumentDB Instance | ✅ Exists | None |
| Private Endpoint | ✅ Configured at 10.2.0.7 | None |
| VNet Peering | ❌ NOT configured | Run script (Step 1) |
| Private DNS Links | ⚠️ Partial (linked to docdb-vnet only) | Run script (Step 2) |

---

## 🚀 Quick Start — 3 Steps

### Step 1: Run the Setup Script (2 minutes)

```powershell
cd scripts
.\quick-setup-private-connection.ps1
```

Wait for completion. Should show:
- ✓ VNet peering created
- ✓ DNS zones linked

### Step 2: Wait for DNS Propagation (2-3 minutes)

After script completes, wait a few minutes for DNS changes to propagate.

### Step 3: Verify Connectivity (1 minute)

From this VM, run:

```powershell
# Test DNS resolution (should show 10.2.0.7)
Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A

# Test TCP connectivity (should show TcpTestSucceeded: True)
Test-NetConnection -ComputerName 10.2.0.7 -Port 27017
```

If both pass → **Private connection is ready! ✅**

---

## 📚 Documentation Files Created

| File | Purpose |
|------|---------|
| `AZURE_DISCOVERY_REPORT.md` | Full discovery details with all resource names and IDs |
| `quick-setup-private-connection.ps1` | **← RUN THIS** Automated setup script (pre-configured) |
| `setup-private-endpoint.ps1` | Full setup script with additional options |
| `PRIVATE_ENDPOINT_SETUP.md` | Comprehensive manual setup guide |
| `PRIVATE_CONNECTION_QUICK_REFERENCE.md` | Quick lookup and troubleshooting |

---

## ✅ Success Criteria

After setup, verify all these pass:

```powershell
# 1. DNS resolves to private IP
Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A
# Expected: Address: 10.2.0.7

# 2. TCP port is reachable
Test-NetConnection -ComputerName 10.2.0.7 -Port 27017
# Expected: TcpTestSucceeded: True

# 3. SRV records resolve
Resolve-DnsName -Name "fc-d3df9fb90605-000.global.mongocluster.cosmos.azure.com" -Type A
# Expected: Private IP in 10.x range

# 4. Benchmark preflight passes
dotnet run --project src/Bmt.Preflight -- preflight --config config/config.json --target documentdb
# Expected: Exit code 0 (pass or warning, not fail)
```

---

## 📊 Architecture After Setup

```
┌─────────────────────────────┐
│ This VM (vm-dbtest-hpc-0-az2)
│ VNet: 10.4.0.0/24           │
│ Location: az2               │
└──────────────┬──────────────┘
               │
        VNet Peering
        (created by script)
               │
┌──────────────▼──────────────┐
│ DocumentDB VNet             │
│ vm-dbtest-hpc-hpc-0-vnet    │
│ 10.2.0.0/24                 │
└──────────────┬──────────────┘
               │
       ┌───────▼────────┐
       │ Private        │
       │ Endpoint       │
       │ 10.2.0.7:27017 │
       │ pe-docdb-vcore │
       └────────────────┘
              │
       ┌──────▼────────┐
       │  DocumentDB   │
       │  vCore Cluster│
       └───────────────┘

DNS Zones (linked to both VNets):
- privatelink.mongocluster.cosmos.azure.com
- privatelink.mongo.cosmos.azure.com
```

---

## ⏱️ Timeline

| Step | Time | Action |
|------|------|--------|
| **Now** | 2 min | Run `quick-setup-private-connection.ps1` |
| **T+2** | 3 min | Wait for DNS propagation |
| **T+5** | 1 min | Run verification tests |
| **T+6** | ✅ | Private connection is ready! |

---

## 🆘 Troubleshooting

### "DNS resolution failed" or resolves to public IP

```powershell
# Clear DNS cache
ipconfig /flushdns

# Wait 2 more minutes and retry
Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com"
```

### "TCP connection failed" (Test-NetConnection shows False)

```powershell
# Check peering status
az network vnet peering list --resource-group rg-db-test-hpc --vnet-name vm-dbtest-hpc-0-az2-vnet -o table
# Should show: PeeringState = Connected

# If not connected, wait 30 seconds and try again
```

### Preflight check fails

Run smoke test to check basic connectivity:

```powershell
dotnet run --project src/Bmt.Preflight -- preflight --config config/smoke.json --target documentdb --no-preflight
```

---

## 🎯 Next Steps After Private Connection is Ready

1. **Set connection string environment variable** (copy from VM1):
   ```powershell
   $env:BMT_CONN = "mongodb+srv://user:pass@docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com/?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000"
   ```

2. **Run TCP tuning** (from `scripts/vm1-az2-setup-and-run.ps1`, STEP 1)

3. **Run benchmark preflight check**:
   ```powershell
   dotnet run --project src/Bmt.Preflight -- preflight --config config/config.json --target documentdb --warmup
   ```

4. **Run the full benchmark** (steady + burst scenarios)

---

## 📞 Need Help?

- See `AZURE_DISCOVERY_REPORT.md` for full resource details
- See `PRIVATE_ENDPOINT_SETUP.md` for comprehensive manual steps
- See `PRIVATE_CONNECTION_QUICK_REFERENCE.md` for quick lookup
- See `scripts/quick-setup-private-connection.ps1` for automation details

---

## ✨ Summary

**All Azure resources have been discovered and documented.** The setup script (`quick-setup-private-connection.ps1`) is ready to run and pre-configured with all correct resource names. Private connection setup is a simple 2-minute automated process followed by verification tests.

**Ready? Run the script!**

```powershell
cd scripts
.\quick-setup-private-connection.ps1
```

---

**Created:** 2026-06-18  
**Azure CLI Version:** 2.87.0  
**Subscription:** ME-MngEnvMCAP379524-suzyvaque-1  
**Discovery Status:** ✅ Complete
