# Azure DocumentDB Private Endpoint Setup Automation
# 
# This script automates the setup of a private connection between DocumentDB and VM1-az2.
# It performs VNet peering (if needed), links the Private DNS zone, and validates connectivity.
#
# Usage:
#   1. Run on your local machine with Azure CLI and PowerShell installed
#   2. Set the variables below to match your environment
#   3. Authenticate with Azure: az login
#   4. Run this script: .\setup-private-endpoint.ps1
#
# Prerequisites:
#   - Azure CLI installed and authenticated (az login)
#   - Appropriate RBAC permissions (Network Contributor, etc.)
#   - DocumentDB resource and VM1-az2 already exist in Azure

param(
    [switch]$SkipDNSLink,        # Skip DNS zone linking step
    [switch]$SkipPeering,        # Skip VNet peering creation
    [switch]$SkipValidation,     # Skip post-setup validation
    [switch]$Cleanup             # Remove peering and DNS links (use with caution)
)

# ============================================================================
# CONFIGURATION - Auto-discovered from Azure subscription rg-db-test-hpc
# ============================================================================

$Script:Config = @{
    # DocumentDB Resource Group and VNet
    DocDBResourceGroup      = "rg-db-test-hpc"
    DocDBVNet               = "vm-dbtest-hpc-0-vnet"
    DocDBVNetRegion         = "koreacentral"
    DocDBSubnet             = "default"
    
    # DocumentDB Resource Details
    DocDBHostname           = "docdb-dbtest-hpc-0.global.mongocluster.cosmos.azure.com"
    DocDBPrivateIP          = "10.2.0.7"
    DocDBPort               = 27017
    DocDBSrvTarget          = "fc-d3df9fb90605-000.global.mongocluster.cosmos.azure.com"
    
    # VM1-az2 Details (THIS VM)
    VM1ResourceGroup        = "rg-db-test-hpc"
    VM1VNet                 = "vm-dbtest-hpc-0-az2-vnet"
    VM1VNetRegion           = "koreacentral"
    VM1Subnet               = "default"
    
    # Private DNS Zone(s)
    DNSZones                = @(
        "privatelink.mongocluster.cosmos.azure.com",
        "privatelink.mongo.cosmos.azure.com"
    )
    DNSResourceGroup        = "rg-db-test-hpc"
    
    # Peering Configuration
    PeeringName_VM1ToDocDB  = "vm-dbtest-hpc-0-az2-to-docdb"
    PeeringName_DocDBToVM1  = "vm-dbtest-hpc-0-vnet-to-az2"
}

# ============================================================================
# UTILITY FUNCTIONS
# ============================================================================

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "ℹ $Message" -ForegroundColor Cyan
}

function Write-Warning {
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-Error {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Invoke-AzureCLI {
    param(
        [string]$Command,
        [string]$Description
    )
    
    Write-Info $Description
    try {
        $result = Invoke-Expression "az $Command" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed: $Description"
            Write-Error "Error: $($result -join "`n")"
            return $null
        }
        return $result
    }
    catch {
        Write-Error "Exception during: $Description"
        Write-Error $_.Exception.Message
        return $null
    }
}

# ============================================================================
# STEP 1: VERIFY PREREQUISITES
# ============================================================================

function Verify-Prerequisites {
    Write-Info "Verifying prerequisites..."
    
    # Check Azure CLI
    try {
        $version = az --version 2>&1 | Select-Object -First 1
        Write-Success "Azure CLI installed: $version"
    }
    catch {
        Write-Error "Azure CLI not found. Please install: https://learn.microsoft.com/cli/azure/install-azure-cli"
        return $false
    }
    
    # Check if authenticated
    try {
        $account = az account show --query "{Name:name, Subscription:id}" 2>&1
        Write-Success "Azure authenticated: $(($account | ConvertFrom-Json).Name)"
    }
    catch {
        Write-Error "Not authenticated with Azure. Run: az login"
        return $false
    }
    
    # Verify resource groups exist
    Write-Info "Verifying resource groups..."
    
    foreach ($rg in @($Script:Config.DocDBResourceGroup, $Script:Config.VM1ResourceGroup, $Script:Config.DNSResourceGroup)) {
        $exists = az group exists --name $rg 2>&1
        if ($exists -eq "true") {
            Write-Success "Resource group exists: $rg"
        } else {
            Write-Error "Resource group not found: $rg"
            return $false
        }
    }
    
    return $true
}

# ============================================================================
# STEP 2: VERIFY VNET & ENDPOINT
# ============================================================================

function Verify-VNetAndEndpoint {
    Write-Info "Verifying VNet and DocumentDB private endpoint..."
    
    # Check DocumentDB VNet
    Write-Info "Checking DocumentDB VNet: $($Script:Config.DocDBVNet)"
    $docdbVnet = Invoke-AzureCLI `
        "network vnet show --resource-group $($Script:Config.DocDBResourceGroup) --name $($Script:Config.DocDBVNet) --query id" `
        "Fetching DocumentDB VNet"
    
    if (-not $docdbVnet) {
        Write-Error "DocumentDB VNet not found: $($Script:Config.DocDBVNet)"
        return $false
    }
    Write-Success "DocumentDB VNet exists: $docdbVnet"
    
    # Check VM1 VNet
    Write-Info "Checking VM1 VNet: $($Script:Config.VM1VNet)"
    $vm1Vnet = Invoke-AzureCLI `
        "network vnet show --resource-group $($Script:Config.VM1ResourceGroup) --name $($Script:Config.VM1VNet) --query id" `
        "Fetching VM1 VNet"
    
    if (-not $vm1Vnet) {
        Write-Error "VM1 VNet not found: $($Script:Config.VM1VNet)"
        return $false
    }
    Write-Success "VM1 VNet exists: $vm1Vnet"
    
    # Check Private Endpoint
    Write-Info "Checking DocumentDB private endpoint..."
    $endpoint = Invoke-AzureCLI `
        "network private-endpoint list --resource-group $($Script:Config.DocDBResourceGroup) --query `"[?contains(name, 'docdb') || contains(name, 'cosmosdb')].{Name:name, State:privateLinkServiceConnections[0].properties.provisioningState}[0]`"" `
        "Listing private endpoints"
    
    if ($endpoint) {
        $ep = $endpoint | ConvertFrom-Json
        Write-Success "Private endpoint found: $($ep.Name) (State: $($ep.State))"
    } else {
        Write-Warning "No DocumentDB private endpoint found. Verify it exists manually."
    }
    
    return $true
}

# ============================================================================
# STEP 3: CREATE VNET PEERING
# ============================================================================

function Create-VNetPeering {
    param([switch]$Cleanup)
    
    Write-Info ""
    Write-Info "=== VNet Peering Setup ==="
    Write-Info "Creating peering between $($Script:Config.VM1VNet) and $($Script:Config.DocDBVNet)..."
    
    if ($Cleanup) {
        Write-Info "CLEANUP MODE: Removing peering..."
        
        # Remove VM1 → DocDB peering
        Write-Info "Removing peering: $($Script:Config.PeeringName_VM1ToDocDB)"
        az network vnet peering delete `
            --resource-group $Script:Config.VM1ResourceGroup `
            --vnet-name $Script:Config.VM1VNet `
            --name $Script:Config.PeeringName_VM1ToDocDB `
            --yes 2>&1 | Out-Null
        Write-Success "Removed"
        
        # Remove DocDB → VM1 peering
        Write-Info "Removing peering: $($Script:Config.PeeringName_DocDBToVM1)"
        az network vnet peering delete `
            --resource-group $Script:Config.DocDBResourceGroup `
            --vnet-name $Script:Config.DocDBVNet `
            --name $Script:Config.PeeringName_DocDBToVM1 `
            --yes 2>&1 | Out-Null
        Write-Success "Removed"
        
        return
    }
    
    # Check if peering already exists
    Write-Info "Checking if peering already exists..."
    $existingPeering = az network vnet peering list `
        --resource-group $Script:Config.VM1ResourceGroup `
        --vnet-name $Script:Config.VM1VNet `
        --query "[?name=='$($Script:Config.PeeringName_VM1ToDocDB)'].peeringState[0]" -o tsv 2>&1
    
    if ($existingPeering -eq "Connected") {
        Write-Success "Peering already exists and is connected"
        return $true
    } elseif ($existingPeering -eq "Initiated") {
        Write-Warning "Peering exists but not yet connected (state: Initiated)"
        Write-Info "The remote side may need to accept. Waiting 10 seconds..."
        Start-Sleep -Seconds 10
        return $true
    }
    
    # Get full resource IDs
    $docdbVnetId = az network vnet show `
        --resource-group $Script:Config.DocDBResourceGroup `
        --name $Script:Config.DocDBVNet `
        --query id -o tsv
    
    $vm1VnetId = az network vnet show `
        --resource-group $Script:Config.VM1ResourceGroup `
        --name $Script:Config.VM1VNet `
        --query id -o tsv
    
    if (-not $docdbVnetId -or -not $vm1VnetId) {
        Write-Error "Could not retrieve VNet IDs"
        return $false
    }
    
    # Create peering: VM1 → DocDB
    Write-Info "Creating peering: $($Script:Config.VM1VNet) → $($Script:Config.DocDBVNet)"
    $result = Invoke-AzureCLI `
        "network vnet peering create --resource-group $($Script:Config.VM1ResourceGroup) --vnet-name $($Script:Config.VM1VNet) --name $($Script:Config.PeeringName_VM1ToDocDB) --remote-vnet $docdbVnetId --allow-vnet-access --allow-forwarded-traffic" `
        "Creating peering from VM1 VNet to DocDB VNet"
    
    if (-not $result) { return $false }
    Write-Success "Peering created"
    
    # Create reverse peering: DocDB → VM1
    Write-Info "Creating reverse peering: $($Script:Config.DocDBVNet) → $($Script:Config.VM1VNet)"
    $result = Invoke-AzureCLI `
        "network vnet peering create --resource-group $($Script:Config.DocDBResourceGroup) --vnet-name $($Script:Config.DocDBVNet) --name $($Script:Config.PeeringName_DocDBToVM1) --remote-vnet $vm1VnetId --allow-vnet-access --allow-forwarded-traffic" `
        "Creating peering from DocDB VNet to VM1 VNet"
    
    if (-not $result) { return $false }
    Write-Success "Reverse peering created"
    
    # Verify both sides are connected
    Start-Sleep -Seconds 5
    $state = az network vnet peering list `
        --resource-group $Script:Config.VM1ResourceGroup `
        --vnet-name $Script:Config.VM1VNet `
        --query "[0].peeringState" -o tsv 2>&1
    
    if ($state -eq "Connected") {
        Write-Success "Peering is now Connected"
        return $true
    } else {
        Write-Warning "Peering state: $state (may need acceptance on remote side)"
        return $true
    }
}

# ============================================================================
# STEP 4: LINK PRIVATE DNS ZONE
# ============================================================================

function Link-PrivateDNSZone {
    param([switch]$Cleanup)
    
    Write-Info ""
    Write-Info "=== Private DNS Zone Linking ==="
    
    # Get VM1 VNet ID
    $vm1VnetId = az network vnet show `
        --resource-group $Script:Config.VM1ResourceGroup `
        --name $Script:Config.VM1VNet `
        --query id -o tsv
    
    if (-not $vm1VnetId) {
        Write-Error "Could not retrieve VM1 VNet ID"
        return $false
    }
    
    foreach ($zone in $Script:Config.DNSZones) {
        Write-Info "Processing DNS zone: $zone"
        
        # Check if zone exists
        $zoneExists = az network private-dns zone show `
            --resource-group $Script:Config.DNSResourceGroup `
            --name $zone `
            --query id -o tsv 2>&1
        
        if (-not $zoneExists -or $zoneExists -like "*not found*") {
            Write-Warning "DNS zone not found: $zone (may not be needed for this cluster)"
            continue
        }
        
        Write-Success "DNS zone exists: $zone"
        
        $linkName = "vm1-az2-link-$(($zone -split '\.')[0])"
        
        if ($Cleanup) {
            Write-Info "CLEANUP MODE: Removing DNS link: $linkName"
            az network private-dns link vnet delete `
                --resource-group $Script:Config.DNSResourceGroup `
                --zone-name $zone `
                --name $linkName `
                --yes 2>&1 | Out-Null
            Write-Success "Removed"
            continue
        }
        
        # Check if link already exists
        Write-Info "Checking if link already exists: $linkName"
        $existingLink = az network private-dns link vnet show `
            --resource-group $Script:Config.DNSResourceGroup `
            --zone-name $zone `
            --name $linkName `
            --query id -o tsv 2>&1
        
        if ($existingLink -and $existingLink -notlike "*not found*") {
            Write-Success "Link already exists: $linkName"
            continue
        }
        
        # Create link
        Write-Info "Creating DNS zone link: $linkName"
        $result = Invoke-AzureCLI `
            "network private-dns link vnet create --resource-group $($Script:Config.DNSResourceGroup) --zone-name $zone --name $linkName --virtual-network $vm1VnetId --registration-enabled false" `
            "Linking Private DNS zone to VM1 VNet"
        
        if (-not $result) { return $false }
        Write-Success "Link created"
    }
    
    return $true
}

# ============================================================================
# STEP 5: VALIDATE CONNECTIVITY
# ============================================================================

function Validate-Connectivity {
    Write-Info ""
    Write-Info "=== Connectivity Validation ==="
    Write-Info "Note: Full validation requires running tests from VM1-az2. Below is a summary of configuration."
    
    # Summarize setup
    Write-Info "Configuration Summary:"
    Write-Host "  DocumentDB Host: $($Script:Config.DocDBHostname)"
    Write-Host "  DocumentDB Private IP: $($Script:Config.DocDBPrivateIP)"
    Write-Host "  DocumentDB Port: $($Script:Config.DocDBPort)"
    Write-Host "  VM1 VNet: $($Script:Config.VM1VNet)"
    Write-Host "  DocDB VNet: $($Script:Config.DocDBVNet)"
    Write-Host "  VNet Peering: $($Script:Config.PeeringName_VM1ToDocDB) ↔ $($Script:Config.PeeringName_DocDBToVM1)"
    Write-Host "  DNS Zones Linked: $($Script:Config.DNSZones -join ', ')"
    
    Write-Info ""
    Write-Info "To complete validation, run these commands from VM1-az2:"
    Write-Host ""
    Write-Host "  # DNS Resolution Test" -ForegroundColor Yellow
    Write-Host "  Resolve-DnsName -Name '$($Script:Config.DocDBHostname)' -Type A"
    Write-Host "  Resolve-DnsName -Name '$($Script:Config.DocDBSrvTarget)' -Type A"
    Write-Host ""
    Write-Host "  # TCP Connectivity Test" -ForegroundColor Yellow
    Write-Host "  Test-NetConnection -ComputerName $($Script:Config.DocDBPrivateIP) -Port $($Script:Config.DocDBPort)"
    Write-Host ""
    Write-Host "  # Benchmark Preflight (if .NET installed)" -ForegroundColor Yellow
    Write-Host "  dotnet run --project src/Bmt.Preflight -- preflight --config config/config.json --target documentdb"
    Write-Host ""
    
    return $true
}

# ============================================================================
# MAIN
# ============================================================================

Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Azure DocumentDB Private Endpoint Setup for VM1-az2          ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Verify prerequisites
if (-not (Verify-Prerequisites)) {
    Write-Error "Prerequisites check failed. Exiting."
    exit 1
}

Write-Host ""

# Verify VNet and endpoint
if (-not (Verify-VNetAndEndpoint)) {
    Write-Error "VNet or endpoint verification failed. Exiting."
    exit 1
}

Write-Host ""

# Create VNet peering (unless skipped)
if ($SkipPeering) {
    Write-Warning "Skipping VNet peering creation (--SkipPeering flag set)"
} else {
    if (-not (Create-VNetPeering -Cleanup:$Cleanup)) {
        Write-Error "VNet peering setup failed. Exiting."
        exit 1
    }
}

Write-Host ""

# Link Private DNS zone (unless skipped)
if ($SkipDNSLink) {
    Write-Warning "Skipping DNS zone linking (--SkipDNSLink flag set)"
} else {
    if (-not (Link-PrivateDNSZone -Cleanup:$Cleanup)) {
        Write-Error "Private DNS zone linking failed. Exiting."
        exit 1
    }
}

Write-Host ""

# Validate connectivity (unless skipped)
if ($SkipValidation) {
    Write-Warning "Skipping validation (--SkipValidation flag set)"
} else {
    Validate-Connectivity
}

Write-Host ""

if ($Cleanup) {
    Write-Warning "Cleanup completed. VNet peering and DNS links have been removed."
} else {
    Write-Success "Private endpoint setup complete!"
    Write-Info "Next steps:"
    Write-Info "  1. Verify connectivity from VM1-az2 (see commands above)"
    Write-Info "  2. Follow the runbook in scripts/vm1-az2-setup-and-run.ps1"
}

Write-Host ""
