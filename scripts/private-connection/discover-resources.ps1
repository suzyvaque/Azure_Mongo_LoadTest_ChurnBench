# Discover Azure Resources in rg-db-test-hpc
# Run this after az login completes

$env:PATH = "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin;$env:PATH"

Write-Host "=== Azure Resource Discovery ===" -ForegroundColor Cyan
Write-Host ""

# Get subscription info
Write-Host "Getting subscription info..." -ForegroundColor Cyan
$subInfo = az account show | ConvertFrom-Json
Write-Host "Subscription: $($subInfo.name) ($($subInfo.id))"
Write-Host ""

# Get all resources in rg-db-test-hpc
Write-Host "Resources in rg-db-test-hpc:" -ForegroundColor Cyan
$resources = az resource list --resource-group rg-db-test-hpc -o json | ConvertFrom-Json
$resources | Select-Object name, type, location | Format-Table -AutoSize

Write-Host ""
Write-Host "=== DocumentDB / Cosmos DB Details ===" -ForegroundColor Cyan

# Find Cosmos DB account
$cosmosAccounts = $resources | Where-Object { $_.type -like "*/databaseAccounts" }
foreach ($cosmos in $cosmosAccounts) {
    Write-Host "Found: $($cosmos.name)"
    Write-Host "  Type: $($cosmos.type)"
    Write-Host "  ID: $($cosmos.id)"
    
    # Get connection strings
    $connStrings = az cosmosdb keys list --resource-group rg-db-test-hpc --name $cosmos.name --type connection-strings -o json 2>&1
    if ($connStrings -like "*error*" -or $connStrings -like "*not found*") {
        Write-Host "  (Note: Connection strings require additional permissions)"
    } else {
        $connInfo = $connStrings | ConvertFrom-Json
        Write-Host "  Primary Connection String: $($connInfo.connectionStrings[0].connectionString.Substring(0,80))..."
    }
}

Write-Host ""
Write-Host "=== Network Resources ===" -ForegroundColor Cyan

# Find VNets
$vnets = $resources | Where-Object { $_.type -like "*/virtualNetworks" }
Write-Host "Virtual Networks:"
foreach ($vnet in $vnets) {
    Write-Host "  - $($vnet.name)"
}

# Find Private Endpoints
$endpoints = $resources | Where-Object { $_.type -like "*/privateEndpoints" }
Write-Host ""
Write-Host "Private Endpoints:"
if ($endpoints) {
    foreach ($ep in $endpoints) {
        Write-Host "  - $($ep.name)"
        
        # Get private endpoint details
        $epDetails = az network private-endpoint show --resource-group rg-db-test-hpc --name $ep.name -o json 2>&1 | ConvertFrom-Json
        Write-Host "    VNet: $($epDetails.virtualNetwork.id -split '/' | Select-Object -Last 1)"
        Write-Host "    Subnet: $($epDetails.subnet.id -split '/' | Select-Object -Last 1)"
        
        # Get private IP
        $nicId = $epDetails.networkInterfaces[0].id
        $nic = az network nic show --ids $nicId -o json 2>&1 | ConvertFrom-Json
        $privateIps = $nic.ipConfigurations | Select-Object -ExpandProperty privateIPAddress
        Write-Host "    Private IPs: $($privateIps -join ', ')"
    }
} else {
    Write-Host "  (No private endpoints found)"
}

# Find Private DNS Zones
Write-Host ""
Write-Host "Private DNS Zones:"
$dnsZones = $resources | Where-Object { $_.type -like "*/privateDnsZones" }
if ($dnsZones) {
    foreach ($zone in $dnsZones) {
        Write-Host "  - $($zone.name)"
    }
} else {
    Write-Host "  (No private DNS zones in this RG - may be in shared RG)"
}

Write-Host ""
Write-Host "=== Current VM Details ===" -ForegroundColor Cyan

# Find this VM
$vms = $resources | Where-Object { $_.type -like "*/virtualMachines" }
foreach ($vm in $vms) {
    Write-Host "VM: $($vm.name)"
    $vmDetails = az vm show --resource-group rg-db-test-hpc --name $vm.name -o json 2>&1 | ConvertFrom-Json
    
    # Get network interface
    if ($vmDetails.networkProfile.networkInterfaces) {
        $nicRef = $vmDetails.networkProfile.networkInterfaces[0].id
        $nic = az network nic show --ids $nicRef -o json 2>&1 | ConvertFrom-Json
        
        Write-Host "  NIC: $($nic.name)"
        Write-Host "  VNet: $($nic.ipConfigurations[0].subnet.id -split '/' | Select-Object -Index -3)"
        Write-Host "  Subnet: $($nic.ipConfigurations[0].subnet.id -split '/' | Select-Object -Last 1)"
        Write-Host "  Private IPs: $($nic.ipConfigurations | Select-Object -ExpandProperty privateIPAddress | Join-String -Separator ', ')"
    }
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Green
Write-Host "Total resources found: $($resources.Count)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Note the DocumentDB hostname and private endpoint IP above"
Write-Host "2. Update scripts/setup-private-endpoint.ps1 with discovered resource names"
Write-Host "3. Run the setup script to configure private connectivity"
