# `infra/documentdb-private-endpoint` — DocumentDB private connection for VM1-az2

Establishes a **private connection** between **Azure DocumentDB (Cosmos vCore)** and the load-generator
VM (**VM1-az2**) before running the connection-churn benchmark.

This folder is self-contained:
- **`README.md`** (this file) — the manual, step-by-step procedure and validation checklist.
- **`setup-private-endpoint.ps1`** — automation that performs the same VNet peering + DNS linking + validation
  (`.\setup-private-endpoint.ps1`; `-Cleanup` to tear it back down).

> Companion to [`infra/cosmos`](../cosmos/README.md) (which re-provisions the Cosmos RU backend). Both live
> under `infra/` because they **provision Azure resources**; host-side run helpers live under `scripts/`.

## Overview

This document describes the steps to establish a private connection between **Azure DocumentDB (Cosmos vCore)** and **VM1-az2** before running the connection-churn benchmark.

**Architecture:**
- **DocumentDB** (vCore model) running in `vm-dbtest-hpc-0-vnet` with a private endpoint at `10.2.0.7:27017`
- **VM1-az2** (new Windows Server 2025 VM) in `az2` availability zone, likely in a separate VNet
- **Private DNS zone** `privatelink.mongocluster.cosmos.azure.com` (may also include `privatelink.mongo.cosmos.azure.com`)
- **Goal:** DNS SRV resolution and TCP traffic must flow **privately** (RFC1918), not over the public internet

## Prerequisites

Before proceeding, verify:

1. ✅ **DocumentDB Resource Exists**
   - Resource name: `docdb-dbtest-hpc-0` (or similar)
   - vCore model, region: (e.g., East US)
   - Private endpoint: `10.2.0.7` in `vm-dbtest-hpc-0-vnet`
   - Mongo hostname: `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com`
   - SRV target: `fc-d3df9fb90605-000.global.mongocluster.cosmos.azure.com` on port 10260

2. ✅ **VM1-az2 Exists and is Ready**
   - OS: Windows Server 2025
   - Region/AZ: `az2` (verify via Azure portal: Availability zones)
   - Network: Assigned to a VNet (e.g., `vm1-az2-vnet` or existing peered VNet)
   - Outbound rules: Windows Firewall allows TCP port 27017

3. ✅ **Network Connectivity**
   - If VM1-az2 is in the **same VNet** as DocumentDB's private endpoint (both in `vm-dbtest-hpc-0-vnet`):
     - No peering needed; proceed to **Step 2** (DNS linking)
   - If VM1-az2 is in a **different VNet**:
     - VNet peering must already exist or be created (see **Step 1**)
     - Both VNets must have routes to reach each other's subnets

---

## Step 1: Establish VNet Connectivity (If VM1-az2 is in a different VNet)

**Skip this step if VM1-az2 is already in `vm-dbtest-hpc-0-vnet`.**

### Option A: Use Existing VNet Peering
If peering already exists between the two VNets:
- Verify route tables on both sides allow `10.2.0.7` traffic
- Proceed to Step 2

### Option B: Create New VNet Peering
If peering does **not** exist:

**Using Azure CLI:**
```powershell
# Set variables
$ResourceGroup = "your-rg-name"
$VM1VNet = "vm1-az2-vnet"           # VM1-az2's VNet
$DocDBVNet = "vm-dbtest-hpc-0-vnet" # DocumentDB's VNet
$DocDBRG = "dbtest-rg"              # DocumentDB resource group

# Create peering from VM1 VNet → DocumentDB VNet
az network vnet peering create `
  --resource-group $ResourceGroup `
  --vnet-name $VM1VNet `
  --name "vm1-to-docdb" `
  --remote-vnet "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/$DocDBRG/providers/Microsoft.Network/virtualNetworks/$DocDBVNet" `
  --allow-vnet-access `
  --allow-forwarded-traffic

# Create reverse peering from DocumentDB VNet → VM1 VNet
az network vnet peering create `
  --resource-group $DocDBRG `
  --vnet-name $DocDBVNet `
  --name "docdb-to-vm1" `
  --remote-vnet "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/$ResourceGroup/providers/Microsoft.Network/virtualNetworks/$VM1VNet" `
  --allow-vnet-access `
  --allow-forwarded-traffic

# Verify peering status
az network vnet peering list --resource-group $ResourceGroup --vnet-name $VM1VNet -o table
az network vnet peering list --resource-group $DocDBRG --vnet-name $DocDBVNet -o table
```

Both peerings should show `PeeringState: Connected`.

---

## Step 2: Link Private DNS Zone to VM1-az2's VNet

The private DNS zone `privatelink.mongocluster.cosmos.azure.com` contains the A record mapping the DocumentDB hostname to its private endpoint IP (`10.2.0.7`). This zone must be linked to VM1-az2's VNet so that DNS queries from VM1 resolve privately.

### Using Azure Portal

1. Go to **Azure Portal** → **Private DNS zones**
2. Select **`privatelink.mongocluster.cosmos.azure.com`** (or `privatelink.mongo.cosmos.azure.com` if both exist)
3. On the left sidebar, click **Virtual network links**
4. Click **+ Add** (or **+ Create**)
5. Fill in:
   - **Link name:** e.g., `vm1-az2-vnet-link`
   - **Virtual network:** Select VM1-az2's VNet
   - **Enable auto-registration:** No (leave unchecked)
6. Click **OK**
7. Repeat for any other private DNS zones covering this hostname

### Using Azure CLI

```powershell
# Set variables
$DNSZone = "privatelink.mongocluster.cosmos.azure.com"
$VM1VNet = "vm1-az2-vnet"
$ResourceGroup = "your-rg-name"
$DNSResourceGroup = "dns-resource-group"  # where the Private DNS zone lives

# Link the DNS zone to VM1-az2's VNet
az network private-dns link vnet create `
  --resource-group $DNSResourceGroup `
  --zone-name $DNSZone `
  --name "vm1-az2-link" `
  --virtual-network "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/$ResourceGroup/providers/Microsoft.Network/virtualNetworks/$VM1VNet" `
  --registration-enabled false

# Verify the link exists
az network private-dns link vnet list --resource-group $DNSResourceGroup --zone-name $DNSZone -o table
```

**Expected output:** Link should show `VirtualNetworkLinkState: Succeeded`

---

## Step 3: Verify Private Endpoint Configuration

Confirm the private endpoint exists and is correctly configured:

```powershell
# List private endpoints in the DocumentDB resource group
az network private-endpoint list `
  --resource-group dbtest-rg `
  --query "[?name | contains(name, 'cosmosdb') || contains(name, 'docdb')].{Name:name, PrivateIP:privateEndpointConnections[0].properties.privateEndpoint.id, State:privateLinkServiceConnections[0].properties.provisioningState}" `
  -o table

# Expected: One endpoint with Name like "docdb-private-endpoint" and private IP 10.2.0.7
```

---

## Step 4: Verify Connectivity from VM1-az2

Once DNS linking is complete, run these checks **from VM1-az2** (RDP or via Azure Bastion):

### 4.1 DNS Resolution Test

```powershell
# Should resolve to 10.2.0.7 (private IP)
Resolve-DnsName -Name "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com" -Type A
Resolve-DnsName -Name "fc-d3df9fb90605-000.global.mongocluster.cosmos.azure.com" -Type A  # SRV target

# Should show only A records pointing to 10.2.0.x, NOT public IPs
```

**Expected output:**
```
Name                                              Type   TTL   Section   IPAddress
----                                              ----   ---   -------   ---------
docdb-dbtest-hpc-0.global.mongocluster.cosmos...  A      300   Answer    10.2.0.7
```

### 4.2 TCP Connectivity Test

```powershell
$DocDbPrivateIP = "10.2.0.7"
$DocDbPort = 27017

# Test TCP port reachability
Test-NetConnection -ComputerName $DocDbPrivateIP -Port $DocDbPort

# Should show: TcpTestSucceeded: True
```

### 4.3 MongoDB Driver Connectivity Test (Optional)

If MongoDB C# driver is installed, test actual connection:

```powershell
$connectionString = $env:BMT_CONN  # Must be set on VM1-az2 as per setup script

# Quick test: Try to connect and fetch server info
dotnet run --project src/Bmt.Preflight -- preflight --config config/smoke.json --target documentdb
```

---

## Step 5: Troubleshooting

### DNS resolution fails (resolves to public IP or fails to resolve)

**Cause:** Private DNS zone link is missing or not yet active  
**Fix:**
1. Verify the virtual network link exists in the Private DNS zone (Step 2)
2. Wait 5–10 minutes for the link to propagate
3. Restart the VM or flush DNS cache: `ipconfig /flushdns`
4. Retry: `Resolve-DnsName "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com"`

### TCP connectivity fails (port 27017 is unreachable)

**Causes:**
1. VNet peering is not active
2. Network Security Group (NSG) rules block traffic
3. DocumentDB private endpoint is not configured correctly

**Fix:**
1. Verify VNet peering is `Connected` (Step 1)
2. Check NSG rules on the DocumentDB private endpoint's subnet:
   ```powershell
   az network nsg rule list --resource-group dbtest-rg --nsg-name "docdb-nsg" -o table
   ```
   Ensure an **inbound rule** allows traffic from VM1-az2's subnet on port 27017.
3. Check if the DocumentDB private endpoint status is `Approved` (in Azure Portal)

### MongoDB Connection Error in benchmark

If the benchmark fails with a connection error after verifying DNS and TCP:

1. Ensure the connection string includes `retrywrites=false`:
   ```
   mongodb+srv://<user>:<pass>@docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com/?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000
   ```
2. Verify credentials are correct (test on VM1 first)
3. Run the preflight check to identify any compatibility issues:
   ```powershell
   dotnet run --project src/Bmt.Preflight -- preflight --config config/config.json --target documentdb --warmup
   ```

---

## Summary Checklist

- [ ] DocumentDB private endpoint exists at `10.2.0.7`
- [ ] VM1-az2 is in the same VNet as the endpoint, or VNet peering is active
- [ ] Private DNS zone `privatelink.mongocluster.cosmos.azure.com` is linked to VM1-az2's VNet
- [ ] DNS resolution from VM1-az2 returns `10.2.0.7` for `docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com`
- [ ] TCP test `Test-NetConnection -ComputerName 10.2.0.7 -Port 27017` succeeds
- [ ] Preflight check passes (or at least no connectivity errors)
- [ ] Environment variable `$env:BMT_CONN` is set on VM1-az2

Once all checks pass, you can proceed with running the benchmark on VM1-az2 using the runbook in `scripts/vm1-az2-setup-and-run.ps1`.

---

## Next Steps

After private connection is established:

1. **Set up VM1-az2** following `scripts/vm1-az2-setup-and-run.ps1`
2. **Run the benchmark** with `dotnet run --project src/Bmt.LoadGen -- test --config config/production/full-workload.json --target documentdb --scenario both`
3. **Collect results** and commit to the repo

---

## References

- [Azure Private Endpoints Documentation](https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-overview)
- [Azure Private DNS Zones](https://learn.microsoft.com/en-us/azure/dns/private-dns-overview)
- [VNet Peering](https://learn.microsoft.com/en-us/azure/virtual-network/virtual-network-peering-overview)
- [DocumentDB Networking](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-configure-private-endpoints)
