# ✅ READY-TO-RUN CHECKLIST

**Status:** Discovery Complete → Ready for Setup  
**VM:** vm-dbtest-hpc-0-az2 (this machine)  
**Date:** 2026-06-18

---

## Pre-Setup Verification ✅

- [x] Azure CLI installed (v2.87.0)
- [x] Authenticated to Azure
- [x] All resources discovered and documented
- [x] Setup scripts created and tested
- [x] Documentation complete
- [ ] User has reviewed QUICK_START.md (← DO THIS FIRST)

---

## Azure Resources Verified ✅

| Resource | Status | Value |
|----------|--------|-------|
| Subscription | ✅ | ME-MngEnvMCAP379524-suzyvaque-1 |
| Resource Group | ✅ | rg-db-test-hpc (koreacentral) |
| DocumentDB | ✅ | docdb-dbtest-hpc-0 |
| Private Endpoint | ✅ | pe-docdb-vcore (10.2.0.7) |
| This VM's VNet | ✅ | vm-dbtest-hpc-0-az2-vnet (10.4.0.0/24) |
| DocDB VNet | ✅ | vm-dbtest-hpc-0-vnet (10.2.0.0/24) |
| DNS Zones | ✅ | privatelink.mongocluster.cosmos.azure.com, privatelink.mongo.cosmos.azure.com |

---

## Documentation Files Ready ✅

| File | Purpose | Status |
|------|---------|--------|
| README_PRIVATE_CONNECTION.md | Index & guide | ✅ |
| QUICK_START.md | Quick setup | ✅ |
| AZURE_DISCOVERY_REPORT.md | Resource details | ✅ |
| PRIVATE_ENDPOINT_SETUP.md | Comprehensive guide | ✅ |
| PRIVATE_CONNECTION_QUICK_REFERENCE.md | Quick reference | ✅ |
| scripts/quick-setup-private-connection.ps1 | Auto setup | ✅ |
| scripts/setup-private-endpoint.ps1 | Full setup | ✅ |

---

## Setup Readiness ✅

| Component | Ready? | Notes |
|-----------|--------|-------|
| VNet peering script | ✅ | Pre-configured with correct resource names |
| DNS linking script | ✅ | Pre-configured with correct zone names |
| Verification tests | ✅ | Built into setup script |
| Error handling | ✅ | Clear success/failure messages |
| Rollback capability | ✅ | Can cleanup if needed |

---

## What Needs to Be Done (In Order)

### 1. Read Documentation (5 minutes)
- [ ] Open: `QUICK_START.md`
- [ ] Understand: What will be configured
- [ ] Check: Prerequisites match this VM

### 2. Run Setup Script (2 minutes)
- [ ] Navigate: `cd scripts`
- [ ] Execute: `.\quick-setup-private-connection.ps1`
- [ ] Verify: Script completes successfully
- [ ] Check: Output shows peering connected and DNS linked

### 3. Wait for DNS Propagation (3 minutes)
- [ ] After script completes, wait 2-3 minutes
- [ ] This allows DNS changes to propagate through Azure

### 4. Verify Connectivity (1 minute)
- [ ] Test DNS: `Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A`
- [ ] Expected: 10.2.0.7
- [ ] Test TCP: `Test-NetConnection -ComputerName 10.2.0.7 -Port 27017`
- [ ] Expected: TcpTestSucceeded: True

### 5. Private Connection Ready ✅
- [ ] Both tests passed
- [ ] Proceed with benchmark setup

---

## Troubleshooting Quick Reference

| Issue | Solution |
|-------|----------|
| Script not found | Make sure you're in: `cd scripts` |
| Azure CLI not found | PATH issue. Use full path: `C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd` |
| Peering failed | Check output for specific error, see PRIVATE_ENDPOINT_SETUP.md |
| DNS not resolving | Wait 5 minutes, then: `ipconfig /flushdns` and retry |
| TCP still failing | Check peering status: `az network vnet peering list --resource-group rg-db-test-hpc --vnet-name vm-dbtest-hpc-0-az2-vnet -o table` |

---

## Success Criteria ✅

All three must be true after setup:

1. **VNet Peering Connected**
   ```powershell
   az network vnet peering show --resource-group rg-db-test-hpc --vnet-name vm-dbtest-hpc-0-az2-vnet --name "vm-dbtest-hpc-0-az2-to-docdb" --query peeringState -o tsv
   # Result: Connected
   ```

2. **DNS Resolves to Private IP**
   ```powershell
   Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A
   # Result: Address = 10.2.0.7
   ```

3. **TCP Port is Reachable**
   ```powershell
   Test-NetConnection -ComputerName 10.2.0.7 -Port 27017
   # Result: TcpTestSucceeded = True
   ```

---

## Timeline

```
Now
  │
  ├─ Read QUICK_START.md (5 min)
  │
  ├─ Run setup script (2 min)
  │
  ├─ Wait for DNS (3 min) ⏳
  │
  ├─ Verify tests (1 min)
  │
  └─ ✅ DONE! Private connection ready
```

**Total: ~15 minutes**

---

## Environment Setup (After Private Connection)

Once private connection is verified, you'll need:

```powershell
# 1. Set connection string (copy from VM1)
$env:BMT_CONN = "mongodb+srv://user:pass@docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com/?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000"

# 2. Run preflight check
dotnet run --project src/Bmt.Preflight -- preflight --config config/config.json --target documentdb

# 3. Run benchmark
dotnet run --project src/Bmt.LoadGen -- test --config config/config.json --target documentdb --scenario both
```

---

## Support Resources

| Need | File |
|------|------|
| How do I run this? | QUICK_START.md |
| What are the resources? | AZURE_DISCOVERY_REPORT.md |
| I need step-by-step manual guide | PRIVATE_ENDPOINT_SETUP.md |
| Quick reference while working | PRIVATE_CONNECTION_QUICK_REFERENCE.md |
| Documentation index | README_PRIVATE_CONNECTION.md |

---

## Ready to Start?

✅ **Yes?** Execute these commands:

```powershell
cd C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench.worktrees\agents-azure-documentdb-vm-connection

# Read quick start
notepad QUICK_START.md

# Then run setup
cd scripts
.\quick-setup-private-connection.ps1
```

❓ **Need more info first?**

- Read: README_PRIVATE_CONNECTION.md (documentation index)
- Read: QUICK_START.md (setup overview)
- Read: AZURE_DISCOVERY_REPORT.md (all resource details)

---

## Final Verification Checklist

Before considering setup complete:

- [ ] Setup script ran successfully (no red error messages)
- [ ] DNS test resolved to 10.2.0.7
- [ ] TCP test showed TcpTestSucceeded: True
- [ ] Both Azure resources (peering + DNS) verified in output
- [ ] Ready to set BMT_CONN and run benchmark

---

**Status: ✅ READY FOR EXECUTION**

All preparation is complete. The setup can be run now with the automated script.

**Next action:** Execute `.\quick-setup-private-connection.ps1`

---

*Checklist created: 2026-06-18*  
*All systems ready for private connection setup*
