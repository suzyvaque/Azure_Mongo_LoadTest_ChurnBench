# Azure DocumentDB Private Connection Setup — Summary

## 📌 Overview

This repository now includes comprehensive documentation and automation tools to establish a **private network connection** between Azure DocumentDB (Cosmos vCore) and VM1-az2 before running the connection-churn benchmark.

### What was Created

1. **PRIVATE_ENDPOINT_SETUP.md** — Detailed step-by-step guide
   - Comprehensive troubleshooting
   - Manual Azure Portal instructions
   - Azure CLI commands for each step
   - Network architecture explanation

2. **setup-private-endpoint.ps1** — Automated PowerShell script
   - Verifies prerequisites
   - Creates or validates VNet peering
   - Links Private DNS zones
   - Validates post-setup connectivity
   - Includes cleanup mode

3. **PRIVATE_CONNECTION_QUICK_REFERENCE.md** — Quick checklist
   - Pre-setup verification
   - Step-by-step quick commands
   - Common troubleshooting
   - Success criteria

---

## 🚀 Quick Start

### For Local Setup (Your Machine)

1. **Edit configuration** in `scripts/setup-private-endpoint.ps1`:
   ```powershell
   # Update these variables to match your environment:
   $Script:Config = @{
       DocDBResourceGroup      = "your-rg"
       VM1ResourceGroup        = "your-rg"
       VM1VNet                 = "your-vm-vnet"
       DNSResourceGroup        = "your-dns-rg"
       # ... etc
   }
   ```

2. **Run the automation script**:
   ```powershell
   az login
   cd scripts
   .\setup-private-endpoint.ps1
   ```

3. **Verify on VM1-az2** (RDP into the VM):
   ```powershell
   # DNS test (should resolve to private IP 10.2.0.7)
   Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com"
   
   # TCP test (should show TcpTestSucceeded: True)
   Test-NetConnection -ComputerName 10.2.0.7 -Port 27017
   ```

### For Manual Setup (Azure Portal)

Follow the detailed instructions in **PRIVATE_ENDPOINT_SETUP.md** (Steps 1–5).

---

## 📋 What the Setup Does

### 1. VNet Peering
- Connects VM1-az2's VNet to DocumentDB's VNet
- Enables routing of RFC1918 traffic between both networks
- Creates bidirectional peering (VM1↔DocDB)

### 2. Private DNS Zone Linking
- Links `privatelink.mongocluster.cosmos.azure.com` to VM1-az2's VNet
- Ensures `mongodb+srv://docdb-dbtest-hpc-0...` resolves to private IP `10.2.0.7` (not public)
- Covers both main zone and any secondary zones (e.g., `privatelink.mongo.cosmos.azure.com`)

### 3. Connectivity Validation
- Tests DNS resolution from VM1-az2
- Tests TCP reachability to port 27017
- Confirms no connectivity errors before benchmark starts

---

## 🔐 Security Model

**Private connection means:**
- ✅ DocumentDB is **not exposed** to the public internet
- ✅ All traffic between VM1-az2 and DocumentDB stays **within the Azure backbone**
- ✅ Credentials are never transmitted over public networks
- ✅ Connection strings can safely use internal hostnames

**Note:** Credentials in the connection string must still be protected (use environment variables, not hardcoded).

---

## 📊 Network Architecture (After Setup)

```
┌────────────────────────────────┐      ┌────────────────────────────────┐
│ vm1-az2-vnet                   │      │ vm-dbtest-hpc-0-vnet           │
│ (VM1-az2's VNet)               │◄────►│ (DocumentDB's VNet)            │
│                                │      │                                │
│  ┌──────────────────────────┐  │      │  ┌──────────────────────────┐ │
│  │ VM1-az2                  │  │      │  │ DocumentDB vCore        │ │
│  │ Windows Server 2025      │  │      │  │ Cluster                 │ │
│  │ (Az2 region)             │  │      │  └──────────────────────────┘ │
│  │                          │  │      │           │                    │
│  └──────────────────────────┘  │      │  ┌────────▼─────────────────┐ │
│           │                     │      │  │ Private Endpoint        │ │
│   queries ▼ (via private DNS)   │      │  │ 10.2.0.7:27017         │ │
│  ┌──────────────────────────┐  │      │  └────────────────────────┘ │
│  │ DNS resolver → private   │  │      │                                │
│  │ 10.2.0.7 (not public IP) │  │      └────────────────────────────────┘
│  └──────────────────────────┘  │
└────────────────────────────────┘

Private DNS Zone: privatelink.mongocluster.cosmos.azure.com
└─ Linked to both vm1-az2-vnet and vm-dbtest-hpc-0-vnet
   (Ensures resolution is private regardless of which VNet initiates query)
```

---

## ✅ Verification Steps

After running the setup, verify:

| Check | Command | Expected | Status |
|-------|---------|----------|--------|
| **DNS → Private IP** | `Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com"` | Returns `10.2.0.7` (not public IP) | 🔄 |
| **TCP Port Open** | `Test-NetConnection -ComputerName 10.2.0.7 -Port 27017` | `TcpTestSucceeded: True` | 🔄 |
| **SRV Target** | `Resolve-DnsName -Name "fc-d3df9fb90605-000.global.mongocluster.cosmos.azure.com" -Type A` | Returns private IP in 10.x range | 🔄 |
| **Preflight Pass** | `dotnet run --project src/Bmt.Preflight -- preflight --target documentdb` | Exit code 0 (pass/warn, not fail) | 🔄 |

---

## 🛠️ Common Tasks

### Run Setup Script
```powershell
cd scripts
.\setup-private-endpoint.ps1
```

### Skip DNS Linking (if you'll do it manually)
```powershell
.\setup-private-endpoint.ps1 -SkipDNSLink
```

### Skip Peering (if already exists)
```powershell
.\setup-private-endpoint.ps1 -SkipPeering
```

### Check Peering Status
```powershell
az network vnet peering list --resource-group vm1-rg --vnet-name vm1-az2-vnet -o table
```

### Check DNS Zone Links
```powershell
az network private-dns link vnet list --resource-group dns-rg --zone-name "privatelink.mongocluster.cosmos.azure.com" -o table
```

### Cleanup (Remove Peering & DNS Links)
```powershell
.\setup-private-endpoint.ps1 -Cleanup
```

---

## 📚 Next Steps After Setup

Once private connection is verified:

1. **Set environment variables** on VM1-az2 (from `scripts/vm1-az2-setup-and-run.ps1`, STEP 4)
2. **Run TCP tuning** (STEP 1 of runbook)
3. **Install .NET 8 SDK** (STEP 2 of runbook)
4. **Run preflight checks** to gate the benchmark
5. **Run the full benchmark** (steady + burst scenarios)

See `scripts/vm1-az2-setup-and-run.ps1` for the complete runbook.

---

## 🔗 Reference Documents

| Document | Purpose |
|----------|---------|
| **PRIVATE_ENDPOINT_SETUP.md** | Comprehensive step-by-step guide with troubleshooting |
| **PRIVATE_CONNECTION_QUICK_REFERENCE.md** | Quick checklist and fast lookup |
| **scripts/setup-private-endpoint.ps1** | Automated setup script |
| **scripts/vm1-az2-setup-and-run.ps1** | Full benchmark runbook (after private connection is ready) |
| **README.md** | Benchmark documentation |

---

## ❓ FAQ

**Q: Can I run the setup script multiple times?**  
A: Yes, it's idempotent. It checks if peering/DNS links already exist before creating them.

**Q: What if VM1-az2 is already in the same VNet as DocumentDB?**  
A: Skip VNet peering with `.\setup-private-endpoint.ps1 -SkipPeering` and only link DNS zones.

**Q: Can I use manual Azure Portal steps instead of the script?**  
A: Yes, follow PRIVATE_ENDPOINT_SETUP.md (Steps 1–5) for full manual instructions.

**Q: What if DNS still resolves to a public IP after setup?**  
A: See troubleshooting section in PRIVATE_ENDPOINT_SETUP.md. Usually need to wait 5–10 minutes or flush DNS cache.

**Q: How do I remove this setup?**  
A: Run `.\setup-private-endpoint.ps1 -Cleanup` to remove peering and DNS links. This is reversible.

---

## 📞 Support

If setup fails:

1. Check **Troubleshooting** sections in PRIVATE_ENDPOINT_SETUP.md
2. Verify prerequisites (RBAC permissions, resources exist, etc.)
3. Check Azure Portal for peering state and DNS zone links
4. Review script output for specific error messages

---

**Setup created:** 2026-06-18  
**Benchmark:** Azure DocumentDB connection-churn load test  
**Targets:** MongoDB (VM), CosmosDB (RU), DocumentDB (vCore)
