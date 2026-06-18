# Azure Resource Discovery Report — rg-db-test-hpc

**Discovery Date:** 2026-06-18  
**Subscription:** ME-MngEnvMCAP379524-suzyvaque-1  
**Resource Group:** rg-db-test-hpc  
**Region:** koreacentral

---

## 🎯 Current Status

This VM (**vm-dbtest-hpc-0-az2** in **az2**) currently **CANNOT** reach DocumentDB privately because:

| Component | Status | Issue |
|-----------|--------|-------|
| DocumentDB Instance | ✅ Configured | `docdb-dbtest-hpc-0` exists with private endpoint at `10.2.0.7` |
| Private Endpoint | ✅ Configured | `pe-docdb-vcore` in `vm-dbtest-hpc-0-vnet` |
| VNet Peering | ❌ **Missing** | No peering between az2-vnet and docdb-vnet |
| Private DNS Links | ⚠️ **Partial** | Linked to docdb-vnet only, **not** to az2-vnet |

**Result:** DNS queries and TCP traffic cannot reach the private endpoint from this VM.

---

## 📊 Azure Resources Discovered

### Databases

| Resource | Type | Location | Details |
|----------|------|----------|---------|
| `docdb-dbtest-hpc-0` | DocumentDB (vCore) | koreacentral | MongoDB-compatible, private endpoint `10.2.0.7:27017` |
| `cosmos-dbtest-hpc-0` | Cosmos DB (RU) | koreacentral | For comparison testing only |

### Virtual Machines

| VM | VNet | Subnet | Location | Purpose |
|----|------|--------|----------|---------|
| `vm-dbtest-hpc-0` | `vm-dbtest-hpc-0-vnet` | `default` (10.2.0.0/24) | koreacentral | Existing DocumentDB host |
| `vm-dbtest-hpc-0-az2` | `vm-dbtest-hpc-0-az2-vnet` | `default` (10.4.0.0/24) | koreacentral, **az2** | **This VM** (benchmark generator) |
| `vm-dbtest-hpc-1-mongo` | `vm-dbtest-hpc-1-mongo-vnet` | `default` | koreacentral, **az3** | MongoDB for comparison |
| `vm-dbtest-hpc-1-mongo-standby` | `vm-dbtest-hpc-1-mongo-vnet` | `default` | koreacentral | MongoDB standby |

### Network Configuration

#### VNet: `vm-dbtest-hpc-0-az2-vnet` (This VM's VNet)
```
CIDR: 10.4.0.0/16
Subnets:
  - default: 10.4.0.0/24
  - AzureBastionSubnet: 10.4.1.0/26
Location: koreacentral
Peering: NONE (needs to be created)
```

#### VNet: `vm-dbtest-hpc-0-vnet` (DocumentDB's VNet)
```
CIDR: 10.2.0.0/16
Subnets:
  - default: 10.2.0.0/24 (contains DocumentDB private endpoint)
  - AzureBastionSubnet: 10.2.1.0/26
Location: koreacentral
Peering: NONE (needs to be created)
```

### Network Endpoints

#### Private Endpoint: `pe-docdb-vcore`
```
Name: pe-docdb-vcore
VNet: vm-dbtest-hpc-0-vnet
Subnet: default
Private IP: 10.2.0.7
Port: 27017 (MongoDB)
Service: DocumentDB (docdb-dbtest-hpc-0)
Status: ✅ Connected
```

#### Private Endpoint: `pe-cosmos-ru`
```
Name: pe-cosmos-ru
VNet: vm-dbtest-hpc-0-vnet
Subnet: default
Private IP: 10.2.0.8
Service: Cosmos DB (cosmos-dbtest-hpc-0)
```

### Private DNS Zones

| Zone | VNets Linked | Details |
|------|--------------|---------|
| `privatelink.mongocluster.cosmos.azure.com` | ✅ `vm-dbtest-hpc-0-vnet` | ❌ NOT linked to `vm-dbtest-hpc-0-az2-vnet` |
| `privatelink.mongo.cosmos.azure.com` | ✅ `vm-dbtest-hpc-0-vnet` | ❌ NOT linked to `vm-dbtest-hpc-0-az2-vnet` |

**Records in zones:**
- `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com` → `10.2.0.7` (in `privatelink.mongocluster.cosmos.azure.com`)
- Cosmos RU MongoDB endpoint (in `privatelink.mongo.cosmos.azure.com`)

---

## 🔧 Required Configuration Steps

### Step 1: Create VNet Peering (REQUIRED)

**What:** Peer `vm-dbtest-hpc-0-az2-vnet` ↔ `vm-dbtest-hpc-0-vnet`  
**Why:** Allows IP traffic to route between the VNets (10.4.x → 10.2.x)

```powershell
# Run on local machine with az CLI
az network vnet peering create `
  --resource-group rg-db-test-hpc `
  --vnet-name vm-dbtest-hpc-0-az2-vnet `
  --name "vm-dbtest-hpc-0-az2-to-docdb" `
  --remote-vnet "/subscriptions/01c04f52-ad2b-4cc0-b77b-61508ec58f51/resourceGroups/rg-db-test-hpc/providers/Microsoft.Network/virtualNetworks/vm-dbtest-hpc-0-vnet" `
  --allow-vnet-access `
  --allow-forwarded-traffic

# Reverse peering
az network vnet peering create `
  --resource-group rg-db-test-hpc `
  --vnet-name vm-dbtest-hpc-0-vnet `
  --name "vm-dbtest-hpc-0-vnet-to-az2" `
  --remote-vnet "/subscriptions/01c04f52-ad2b-4cc0-b77b-61508ec58f51/resourceGroups/rg-db-test-hpc/providers/Microsoft.Network/virtualNetworks/vm-dbtest-hpc-0-az2-vnet" `
  --allow-vnet-access `
  --allow-forwarded-traffic
```

### Step 2: Link Private DNS Zones to az2-vnet (REQUIRED)

**What:** Link both private DNS zones to `vm-dbtest-hpc-0-az2-vnet`  
**Why:** Allows DNS queries from this VM to resolve DocumentDB hostname to private IP

```powershell
# Link privatelink.mongocluster.cosmos.azure.com
az network private-dns link vnet create `
  --resource-group rg-db-test-hpc `
  --zone-name privatelink.mongocluster.cosmos.azure.com `
  --name "vm-dbtest-hpc-0-az2-link" `
  --virtual-network "/subscriptions/01c04f52-ad2b-4cc0-b77b-61508ec58f51/resourceGroups/rg-db-test-hpc/providers/Microsoft.Network/virtualNetworks/vm-dbtest-hpc-0-az2-vnet" `
  --registration-enabled false

# Link privatelink.mongo.cosmos.azure.com
az network private-dns link vnet create `
  --resource-group rg-db-test-hpc `
  --zone-name privatelink.mongo.cosmos.azure.com `
  --name "vm-dbtest-hpc-0-az2-link" `
  --virtual-network "/subscriptions/01c04f52-ad2b-4cc0-b77b-61508ec58f51/resourceGroups/rg-db-test-hpc/providers/Microsoft.Network/virtualNetworks/vm-dbtest-hpc-0-az2-vnet" `
  --registration-enabled false
```

---

## 🚀 Quick Setup Commands

The `setup-private-endpoint.ps1` script has been updated with all discovered resource names. To run it:

```powershell
# From local machine
$env:PATH = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin;$env:PATH"
cd scripts
.\setup-private-endpoint.ps1
```

This will:
1. ✅ Verify all resources exist
2. ✅ Create VNet peering (if needed)
3. ✅ Link Private DNS zones (if needed)
4. ✅ Validate connectivity

---

## 🔗 Key Resource References

### DocumentDB Connection String Format

```
mongodb+srv://<user>:<pass>@docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com/?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000
```

**Note:** After private connection is set up, this hostname will resolve to `10.2.0.7` (private IP) when queried from this VM.

### Subscription ID
```
01c04f52-ad2b-4cc0-b77b-61508ec58f51
```

---

## ✅ Verification Checklist

After running setup, verify:

- [ ] **VNet Peering Status**
  ```powershell
  az network vnet peering list --resource-group rg-db-test-hpc --vnet-name vm-dbtest-hpc-0-az2-vnet -o table
  # Should show: PeeringState = Connected
  ```

- [ ] **DNS Zone Links**
  ```powershell
  az network private-dns link vnet list --resource-group rg-db-test-hpc --zone-name privatelink.mongocluster.cosmos.azure.com -o table
  # Should show: vm-dbtest-hpc-0-az2-link linked
  ```

- [ ] **From this VM - DNS Resolution**
  ```powershell
  Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A
  # Should return: 10.2.0.7
  ```

- [ ] **From this VM - TCP Connectivity**
  ```powershell
  Test-NetConnection -ComputerName 10.2.0.7 -Port 27017
  # Should return: TcpTestSucceeded: True
  ```

---

## 📝 Notes

- **Subscription:** All resources are in the same subscription and region (koreacentral)
- **Resource Group:** All resources are in `rg-db-test-hpc`
- **Automation Ready:** The `setup-private-endpoint.ps1` script contains all correct resource names (auto-populated)
- **No Additional Setup Needed:** Private endpoint already exists and is configured; only peering and DNS linking are needed

---

## 🔄 What Happens After Setup

Once private connection is established:

1. **DNS queries** from this VM for `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com` will resolve to `10.2.0.7`
2. **Traffic on port 27017** will route privately through VNet peering (never goes to public internet)
3. **Benchmark can proceed** — run the full connection-churn test workload
4. **Results** will be committed to the benchmark repository

---

**Generated:** 2026-06-18 from Azure CLI discovery  
**Setup Script:** `scripts/setup-private-endpoint.ps1` (updated with discovered values)
