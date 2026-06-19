variable "subscription_id" {
  description = "Azure subscription ID that holds the Cosmos account."
  type        = string
  default     = "01c04f52-ad2b-4cc0-b77b-61508ec58f51"
}

variable "resource_group_name" {
  description = "Resource group for the Cosmos account, private endpoint and DNS zone."
  type        = string
  default     = "rg-db-test-hpc"
}

variable "location" {
  description = "Azure region (Cosmos account is single-region, zone-redundant)."
  type        = string
  default     = "koreacentral"
}

variable "account_name" {
  description = "Cosmos DB account name (also the mongo host prefix: <name>.mongo.cosmos.azure.com)."
  type        = string
  default     = "cosmos-dbtest-hpc-0"
}

variable "database_name" {
  description = "Mongo database that holds the SHARED throughput."
  type        = string
  default     = "bmt_db"
}

variable "collections" {
  description = "Collections to create under the shared-throughput database. Each gets a non-unique ReqId index (Cosmos platform constraint) plus the default _id index."
  type        = list(string)
  default     = ["calc_input", "calc_output"]
}

variable "throughput" {
  description = <<-EOT
    SHARED manual RU/s provisioned on the database. Start a FRESH account cheap (the floor on a brand-new
    account is 400 RU/s). Raise to the test value (e.g. 100000) just before a run with
    scripts/cosmos-ru.ps1 -Set 100000 -Wait, then drop back afterwards. Do NOT bake 100000 in here unless
    you want to pay for it from the moment of apply.
  EOT
  type        = number
  default     = 400
}

# ---- Network: the private endpoint + DNS resolution that make the account reachable privately ----

variable "vnet_resource_group" {
  description = "Resource group of the VNet that hosts the private endpoint subnet."
  type        = string
  default     = "rg-db-test-hpc"
}

variable "vnet_name" {
  description = "VNet that hosts the private endpoint subnet (also linked to the private DNS zone)."
  type        = string
  default     = "vm-dbtest-hpc-0-vnet"
}

variable "subnet_name" {
  description = "Subnet that the private endpoint NIC lives in."
  type        = string
  default     = "default"
}

variable "private_endpoint_name" {
  description = "Name of the private endpoint for the Cosmos (MongoDB) sub-resource."
  type        = string
  default     = "pe-cosmos-ru"
}

variable "dns_link_vnet_ids" {
  description = <<-EOT
    Resource IDs of every VNet that must resolve the Cosmos private FQDN (one link per generator VNet).
    Defaults to empty, which means: link the VNet named in vnet_name (resolved via a data source). Add more
    VNet IDs here if other load-generator hosts (e.g. VM1-az2) live in different VNets.
  EOT
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Tags applied to the Cosmos account (mirrors the original)."
  type        = map(string)
  default = {
    "defaultExperience"    = "Azure Cosmos DB for MongoDB API"
    "hidden-workload-type" = "Production"
  }
}
