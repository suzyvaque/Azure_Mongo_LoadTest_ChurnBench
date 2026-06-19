output "account_name" {
  description = "Cosmos account name."
  value       = var.account_name
}

output "mongo_host" {
  description = "Mongo host FQDN. With public access disabled + the private DNS zone, this resolves to the private endpoint IP from inside a linked VNet."
  value       = "${var.account_name}.mongo.cosmos.azure.com"
}

output "document_endpoint" {
  description = "The account document endpoint."
  value       = try(jsondecode(azapi_resource.account.output).properties.documentEndpoint, null)
}

output "current_throughput_ru" {
  description = "Shared RU/s currently provisioned on the database by this config. Raise to the test value with scripts/cosmos-ru.ps1 before a run."
  value       = var.throughput
}

# The Cosmos primary Mongo connection string already includes ssl=true, replicaSet=globaldb,
# retrywrites=false, maxIdleTimeMS and appName — i.e. exactly the options the harness expects — so it
# can be used verbatim as BMT_CONN_COSMOS. Marked sensitive: it contains the account key.
output "bmt_conn_cosmos" {
  description = "Ready-to-use BMT_CONN_COSMOS connection string (contains the account key)."
  value       = data.azurerm_cosmosdb_account.cosmos.primary_mongodb_connection_string
  sensitive   = true
}

output "set_env_command_hint" {
  description = "How to export the connection string without printing it. Run on the load-generator host."
  value       = "terraform output -raw bmt_conn_cosmos | ForEach-Object { [Environment]::SetEnvironmentVariable('BMT_CONN_COSMOS', $_, 'User') }"
}
