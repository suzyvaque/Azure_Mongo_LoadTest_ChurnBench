# Private Connection Setup — Quick Reference

## 📋 Pre-Setup Checklist (Manual Verification)

Before running the automation script, confirm these manually:

- [ ] **DocumentDB resource exists** in Azure (vCore model)
  - Name: `docdb-dbtest-hpc-0` (or verify actual name)
  - Hostname: `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com`
  - Private endpoint IP: `10.2.0.7` (or verify in Azure portal)
  - Port: 27017
  
- [ ] **VM1-az2 is provisioned** and reachable
  - OS: Windows Server 2025
  - VNet assignment: confirmed in Azure portal
  - Your Azure account has **Network Contributor** or equivalent RBAC role
  
- [ ] **Private endpoint exists** for DocumentDB
  - Location: same VNet as DocumentDB (`vm-dbtest-hpc-0-vnet`)
  - Status: Approved
  - Verify in Azure Portal → Private Link Center → Private Endpoints

---

## 🔧 Setup Steps

### Step 1: Run Automation Script (Local Machine)

```powershell
# Authenticate with Azure
az login

# Navigate to the repo
cd C:\Users\suzyvaque\Desktop\Azure_Mongo_LoadTest_ChurnBench.worktrees\agents-azure-documentdb-vm-connection

# Review the script and update config variables (look for $Script:Config)
# Edit: scripts\setup-private-endpoint.ps1
# Update: DocDBResourceGroup, VM1ResourceGroup, VM1VNet, DNSResourceGroup, etc.

# Run the setup script
.\scripts\setup-private-endpoint.ps1

# Should show:
# ✓ Peering created (or already exists)
# ✓ DNS zone linked (or already exists)
```

### Step 2: Verify from VM1-az2 (RDP into VM)

Once the script completes, RDP into VM1-az2 and run:

```powershell
# Test DNS resolution (should resolve to 10.2.0.7)
Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A

# Test TCP connectivity (should show TcpTestSucceeded: True)
Test-NetConnection -ComputerName 10.2.0.7 -Port 27017 -WarningAction SilentlyContinue
```

### Step 3: Set Benchmark Environment

On VM1-az2, copy the connection string from VM1 and set it:

```powershell
# Copy from VM1: $env:BMT_CONN
# Then on VM1-az2:
$env:BMT_CONN = "mongodb+srv://user:pass@docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com/?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000"

# Verify it's set correctly
$env:BMT_CONN -replace '//[^:]+:[^@]+@', '//****:****@'
# Should show: mongodb+srv://****:****@docdb-dbtest-hpc-0...
```

### Step 4: Run Preflight Smoke Test

```powershell
cd C:\bmt  # or wherever you cloned the repo

dotnet run --project src/Bmt.Preflight -- preflight --config config/smoke.json --target documentdb --no-preflight
```

If this succeeds → **Private connection is working!**

---

## ⚠️ Troubleshooting

### "DNS resolution failed" or resolved to public IP

**Problem:** Private DNS zone link is not active  
**Solution:**
```powershell
# On local machine, re-run linking:
.\scripts\setup-private-endpoint.ps1

# On VM1-az2, clear DNS cache:
ipconfig /flushdns

# Wait 5–10 minutes and retry Resolve-DnsName
```

### "TCP connection failed" / Test-NetConnection shows TcpTestSucceeded: False

**Problem 1:** VNet peering not connected  
**Solution:**
```powershell
# On local machine, check peering state:
az network vnet peering list --resource-group vm1-rg --vnet-name vm1-az2-vnet -o table
# Should show PeeringState: Connected for both directions
```

**Problem 2:** NSG rules blocking traffic  
**Solution:**
```powershell
# Check DocumentDB subnet NSG:
az network nsg list --resource-group dbtest-rg -o table

# Verify inbound rule exists allowing VM1-az2 subnet on port 27017
az network nsg rule list --resource-group dbtest-rg --nsg-name "docdb-nsg" -o table

# If missing, add manually or update NSG in Azure Portal
```

**Problem 3:** DocumentDB private endpoint is not in "Approved" state  
**Solution:**
```powershell
# Check private endpoint status in Azure Portal:
# Private Link Center → Private Endpoints → Look for endpoint in "docdb" RG
# If "Pending", you may need to approve it in DocumentDB's Private Link settings
```

---

## 🧹 Cleanup (If Needed)

To remove all created resources:

```powershell
# Run cleanup mode (removes peering + DNS links)
.\scripts\setup-private-endpoint.ps1 -Cleanup

# This is reversible — you can re-run without -Cleanup to re-create them
```

---

## 📊 Network Diagram (After Setup)

```
┌─────────────────────────────────────────────────────────┐
│ Subscription                                             │
├─────────────────────────────────────────────────────────┤
│                                                           │
│  ┌──────────────────────┐       ┌─────────────────────┐  │
│  │ vm1-az2-vnet         │       │ vm-dbtest-hpc-0-vnet│  │
│  │ (az2 region)         │◄─────►│ (region)            │  │
│  │                      │ Peered │                     │  │
│  │ ┌──────────────────┐ │       │ ┌─────────────────┐ │  │
│  │ │ VM1-az2          │ │       │ │ DocumentDB      │ │  │
│  │ │ 10.x.x.x         │ │       │ │ vCore Cluster   │ │  │
│  │ │                  │ │       │ │                 │ │  │
│  │ └──────────────────┘ │       │ └─────────────────┘ │  │
│  │                      │       │                     │  │
│  └──────────────────────┘       │ ┌─────────────────┐ │  │
│           ▲                       │ │ Private         │ │  │
│           │                       │ │ Endpoint        │ │  │
│     DNS resolves                 │ │ 10.2.0.7:27017 │ │  │
│    (private DNS zone)            │ │                 │ │  │
│           │                       │ └─────────────────┘ │  │
│           │                       │                     │  │
│  ┌────────▼────────────┐       └─────────────────────┘  │
│  │ Private DNS Zone    │                                │
│  │ privatelink.*       │                                │
│  │ mongocluster.*.com  │                                │
│  └─────────────────────┘                                │
│                                                           │
└─────────────────────────────────────────────────────────┘
```

---

## 📚 Additional Resources

- [Azure Private Endpoints Documentation](https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-overview)
- [Azure Private DNS Zones](https://learn.microsoft.com/en-us/azure/dns/private-dns-overview)
- [DocumentDB Network Security](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-configure-private-endpoints)
- [Full Setup Guide](./PRIVATE_ENDPOINT_SETUP.md)

---

## ✅ Success Criteria

When setup is complete:

1. ✅ `Resolve-DnsName` returns `10.2.0.7` (private IP, not public)
2. ✅ `Test-NetConnection` shows `TcpTestSucceeded: True` for port 27017
3. ✅ Preflight smoke test passes without connection errors
4. ✅ `$env:BMT_CONN` is set and connection string is valid
5. ✅ Ready to run full benchmark workload
