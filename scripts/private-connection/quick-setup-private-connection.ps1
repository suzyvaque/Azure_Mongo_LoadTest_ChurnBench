# Quick Setup Script — Run This to Configure Private Connection
# 
# This script configures VNet peering and DNS zone linking for private connectivity
# to DocumentDB from this VM (vm-dbtest-hpc-0-az2).
#
# Usage:
#   .\quick-setup-private-connection.ps1
#
# Requirements:
#   - Azure CLI installed (az command available)
#   - Authenticated to Azure (az login already done)
#   - Appropriate RBAC permissions (Network Contributor)

# Ensure Azure CLI is in PATH
$env:PATH = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin;$env:PATH"

Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Private Connection Setup — DocumentDB                         ║" -ForegroundColor Cyan
Write-Host "║  VM: vm-dbtest-hpc-0-az2 (this machine)                       ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Configuration (from discovery)
$resourceGroup = "rg-db-test-hpc"
$subscriptionId = "01c04f52-ad2b-4cc0-b77b-61508ec58f51"
$az2VNet = "vm-dbtest-hpc-0-az2-vnet"
$docdbVNet = "vm-dbtest-hpc-0-vnet"
$az2VNetId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Network/virtualNetworks/$az2VNet"
$docdbVNetId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Network/virtualNetworks/$docdbVNet"
$dnsZones = @("privatelink.mongocluster.cosmos.azure.com", "privatelink.mongo.cosmos.azure.com")

Write-Host "Configuration:" -ForegroundColor Cyan
Write-Host "  Resource Group: $resourceGroup"
Write-Host "  This VM VNet: $az2VNet"
Write-Host "  DocDB VNet: $docdbVNet"
Write-Host "  DNS Zones: $($dnsZones -join ', ')"
Write-Host ""

# ==========================================================================
# STEP 1: Create VNet Peering
# ==========================================================================

Write-Host "╔ STEP 1: Creating VNet Peering ════════════════════════════════╗" -ForegroundColor Yellow
Write-Host ""

# Check if peering already exists
Write-Host "Checking existing peering..." -ForegroundColor Cyan
$existingPeering = az network vnet peering list --resource-group $resourceGroup --vnet-name $az2VNet --query "[?name=='vm-dbtest-hpc-0-az2-to-docdb'].peeringState[0]" -o tsv 2>&1

if ($existingPeering -eq "Connected") {
    Write-Host "✓ Peering already exists and is connected" -ForegroundColor Green
} else {
    Write-Host "Creating peering: $az2VNet → $docdbVNet" -ForegroundColor Cyan
    az network vnet peering create `
        --resource-group $resourceGroup `
        --vnet-name $az2VNet `
        --name "vm-dbtest-hpc-0-az2-to-docdb" `
        --remote-vnet $docdbVNetId `
        --allow-vnet-access `
        --allow-forwarded-traffic | Out-Null
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ Created: vm-dbtest-hpc-0-az2 → docdb" -ForegroundColor Green
    } else {
        Write-Host "✗ Failed to create first peering" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "Creating reverse peering: $docdbVNet → $az2VNet" -ForegroundColor Cyan
    az network vnet peering create `
        --resource-group $resourceGroup `
        --vnet-name $docdbVNet `
        --name "vm-dbtest-hpc-0-vnet-to-az2" `
        --remote-vnet $az2VNetId `
        --allow-vnet-access `
        --allow-forwarded-traffic | Out-Null
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ Created: docdb → vm-dbtest-hpc-0-az2" -ForegroundColor Green
    } else {
        Write-Host "✗ Failed to create reverse peering" -ForegroundColor Red
        exit 1
    }
    
    Start-Sleep -Seconds 5
}

Write-Host ""

# ==========================================================================
# STEP 2: Link Private DNS Zones
# ==========================================================================

Write-Host "╔ STEP 2: Linking Private DNS Zones ════════════════════════════╗" -ForegroundColor Yellow
Write-Host ""

foreach ($zone in $dnsZones) {
    $linkName = "vm-dbtest-hpc-0-az2-link"
    
    Write-Host "Zone: $zone" -ForegroundColor Cyan
    
    # Check if link already exists
    $existingLink = az network private-dns link vnet show `
        --resource-group $resourceGroup `
        --zone-name $zone `
        --name $linkName `
        --query id -o tsv 2>&1
    
    if ($existingLink -and $existingLink -notlike "*not found*") {
        Write-Host "  ✓ Already linked" -ForegroundColor Green
    } else {
        Write-Host "  Linking $linkName..." -ForegroundColor Cyan
        az network private-dns link vnet create `
            --resource-group $resourceGroup `
            --zone-name $zone `
            --name $linkName `
            --virtual-network $az2VNetId `
            --registration-enabled false | Out-Null
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Linked" -ForegroundColor Green
        } else {
            Write-Host "  ✗ Failed" -ForegroundColor Red
            exit 1
        }
    }
}

Write-Host ""

# ==========================================================================
# STEP 3: Verify Setup
# ==========================================================================

Write-Host "╔ STEP 3: Verifying Configuration ══════════════════════════════╗" -ForegroundColor Yellow
Write-Host ""

# Check peering status
Write-Host "Peering Status:" -ForegroundColor Cyan
$peeringStatus = az network vnet peering show `
    --resource-group $resourceGroup `
    --vnet-name $az2VNet `
    --name "vm-dbtest-hpc-0-az2-to-docdb" `
    --query peeringState -o tsv 2>&1

if ($peeringStatus -eq "Connected") {
    Write-Host "  ✓ Peering is Connected" -ForegroundColor Green
} else {
    Write-Host "  ⚠ Peering state: $peeringStatus" -ForegroundColor Yellow
}

# Check DNS zone links
Write-Host ""
Write-Host "DNS Zone Links:" -ForegroundColor Cyan
foreach ($zone in $dnsZones) {
    $links = az network private-dns link vnet list `
        --resource-group $resourceGroup `
        --zone-name $zone `
        --query "[?name=='vm-dbtest-hpc-0-az2-link'].name" -o tsv 2>&1
    
    if ($links) {
        Write-Host "  ✓ $($zone): linked" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ $($zone): not linked" -ForegroundColor Yellow
    }
}

Write-Host ""

# ==========================================================================
# STEP 4: Next Steps
# ==========================================================================

Write-Host "╔ Setup Complete ═══════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Wait 2-3 minutes for DNS propagation"
Write-Host ""
Write-Host "2. Test DNS resolution (run from this VM):"
Write-Host "   Resolve-DnsName -Name 'docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com'"
Write-Host "   Expected: 10.2.0.7" -ForegroundColor Green
Write-Host ""
Write-Host "3. Test TCP connectivity:"
Write-Host "   Test-NetConnection -ComputerName 10.2.0.7 -Port 27017"
Write-Host "   Expected: TcpTestSucceeded: True" -ForegroundColor Green
Write-Host ""
Write-Host "4. Set BMT_CONN environment variable (from VM1):"
Write-Host "   `$env:BMT_CONN = 'mongodb+srv://...'  # from VM1"
Write-Host ""
Write-Host "5. Run preflight check:"
Write-Host "   dotnet run --project src/Bmt.Preflight -- preflight --config config/config.json --target documentdb"
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Green

Write-Host ""
