provider "azurerm" {
  features {}
  use_oidc = true
}

data "azurerm_client_config" "current" {}

data "azurerm_resource_group" "prod" {
  name = var.resource_group_name
}
