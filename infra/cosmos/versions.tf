terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.116"
    }
    # azapi is used ONLY for the database account so the exact apiProperties.serverVersion (7.0),
    # continuous backup tier, zone redundancy and network flags are reproduced faithfully — these are
    # not all expressible on azurerm_cosmosdb_account across provider versions.
    azapi = {
      source  = "azure/azapi"
      version = "~> 1.15"
    }
  }
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id
}

provider "azapi" {
  subscription_id = var.subscription_id
}
