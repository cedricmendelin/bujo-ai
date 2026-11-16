module "identity" {
  source  = "Azure/avm-res-managedidentity-userassignedidentity/azurerm"
  version = "0.6.0"

  name                = local.identity_name
  location            = var.location
  resource_group_name = data.azurerm_resource_group.prod.name
  tags                = local.common_tags

  enable_telemetry = false
}

module "migration_identity" {
  source  = "Azure/avm-res-managedidentity-userassignedidentity/azurerm"
  version = "0.6.0"

  name                = local.migration_identity_name
  location            = var.location
  resource_group_name = data.azurerm_resource_group.prod.name
  tags                = local.common_tags

  enable_telemetry = false
}
