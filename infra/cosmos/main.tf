# =====================================================================================================
# Cosmos DB for MongoDB (RU) — faithful re-provision of the `cosmos-ru` benchmark backend.
#
# Captured from the live account `cosmos-dbtest-hpc-0` on 2026-06-19:
#   API MongoDB serverVersion 7.0 | Session consistency (5s / 100) | single region koreacentral,
#   zone-redundant | Continuous7Days backup | public access DISABLED | VNet filter ON | TLS 1.2 |
#   automatic failover ON | shared db throughput on bmt_db | collections calc_input/calc_output with
#   _id + ReqId (non-unique) indexes | private endpoint pe-cosmos-ru -> privatelink.mongo.cosmos.azure.com
# =====================================================================================================

data "azurerm_resource_group" "rg" {
  name = var.resource_group_name
}

data "azurerm_subnet" "pe" {
  name                 = var.subnet_name
  virtual_network_name = var.vnet_name
  resource_group_name  = var.vnet_resource_group
}

data "azurerm_virtual_network" "vnet" {
  name                = var.vnet_name
  resource_group_name = var.vnet_resource_group
}

# ---------------------------------------------------------------------------------------------------
# 1) Database account (azapi for exact fidelity)
# ---------------------------------------------------------------------------------------------------
resource "azapi_resource" "account" {
  type      = "Microsoft.DocumentDB/databaseAccounts@2024-08-15"
  name      = var.account_name
  location  = var.location
  parent_id = data.azurerm_resource_group.rg.id
  tags      = var.tags

  body = jsonencode({
    kind = "MongoDB"
    identity = {
      type = "None"
    }
    properties = {
      databaseAccountOfferType = "Standard"
      apiProperties = {
        serverVersion = "7.0"
      }
      capabilities = [
        { name = "EnableMongo" }
      ]
      consistencyPolicy = {
        defaultConsistencyLevel = "Session"
        maxIntervalInSeconds    = 5
        maxStalenessPrefix      = 100
      }
      locations = [
        {
          locationName     = var.location
          failoverPriority = 0
          isZoneRedundant  = true
        }
      ]
      enableAutomaticFailover       = true
      enableMultipleWriteLocations  = false
      publicNetworkAccess           = "Disabled"
      isVirtualNetworkFilterEnabled = true
      virtualNetworkRules = [
        {
          id                               = data.azurerm_subnet.pe.id
          ignoreMissingVNetServiceEndpoint = true
        }
      ]
      minimalTlsVersion = "Tls12"
      networkAclBypass  = "None"
      disableLocalAuth  = false
      backupPolicy = {
        type = "Continuous"
        continuousModeProperties = {
          tier = "Continuous7Days"
        }
      }
    }
  })

  response_export_values = ["properties.documentEndpoint"]
}

# ---------------------------------------------------------------------------------------------------
# 2) Database (shared manual throughput) + collections with the mandatory ReqId index
# ---------------------------------------------------------------------------------------------------
resource "azurerm_cosmosdb_mongo_database" "bmt" {
  name                = var.database_name
  resource_group_name = data.azurerm_resource_group.rg.name
  account_name        = var.account_name
  throughput          = var.throughput

  depends_on = [azapi_resource.account]
}

resource "azurerm_cosmosdb_mongo_collection" "col" {
  for_each = toset(var.collections)

  name                = each.value
  resource_group_name = data.azurerm_resource_group.rg.name
  account_name        = var.account_name
  database_name       = azurerm_cosmosdb_mongo_database.bmt.name

  # Default _id index (system-unique on all backends).
  index {
    keys   = ["_id"]
    unique = true
  }

  # The workload keys every op on ReqId, never the _id point-read. Non-unique on Cosmos by platform
  # constraint; distinct ReqId is still guaranteed because ReqId == _id and _id is system-unique.
  index {
    keys   = ["ReqId"]
    unique = false
  }
}

# ---------------------------------------------------------------------------------------------------
# 3) Private DNS zone + VNet link(s) + the private endpoint (private-only reachability)
# ---------------------------------------------------------------------------------------------------
resource "azurerm_private_dns_zone" "cosmos" {
  name                = "privatelink.mongo.cosmos.azure.com"
  resource_group_name = data.azurerm_resource_group.rg.name
}

locals {
  # Default to linking the PE's own VNet; otherwise honour the explicit list.
  dns_link_vnet_ids = length(var.dns_link_vnet_ids) > 0 ? var.dns_link_vnet_ids : [data.azurerm_virtual_network.vnet.id]
}

resource "azurerm_private_dns_zone_virtual_network_link" "link" {
  for_each = { for idx, id in local.dns_link_vnet_ids : idx => id }

  name                  = "link-${each.key}"
  resource_group_name   = data.azurerm_resource_group.rg.name
  private_dns_zone_name = azurerm_private_dns_zone.cosmos.name
  virtual_network_id    = each.value
  registration_enabled  = false
}

resource "azurerm_private_endpoint" "cosmos" {
  name                = var.private_endpoint_name
  location            = var.location
  resource_group_name = data.azurerm_resource_group.rg.name
  subnet_id           = data.azurerm_subnet.pe.id

  private_service_connection {
    name                           = var.private_endpoint_name
    private_connection_resource_id = azapi_resource.account.id
    is_manual_connection           = false
    subresource_names              = ["MongoDB"]
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.cosmos.id]
  }
}

# ---------------------------------------------------------------------------------------------------
# 4) Read back the connection string (keys) for the connect step
# ---------------------------------------------------------------------------------------------------
data "azurerm_cosmosdb_account" "cosmos" {
  name                = var.account_name
  resource_group_name = data.azurerm_resource_group.rg.name

  depends_on = [azapi_resource.account]
}
