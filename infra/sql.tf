module "sql_server" {
  source  = "Azure/avm-res-sql-server/azurerm"
  version = "0.2.1"

  name                = local.sql_server_name
  location            = var.location
  resource_group_name = data.azurerm_resource_group.prod.name
  server_version      = "12.0"
  managed_identities = {
    system_assigned = true
  }
  azuread_administrator = {
    login_username              = var.sql_entra_admin_login
    object_id                   = var.sql_entra_admin_object_id
    tenant_id                   = data.azurerm_client_config.current.tenant_id
    azuread_authentication_only = true
  }
  public_network_access_enabled = true
  firewall_rules = {
    azure_services = {
      start_ip_address = "0.0.0.0"
      end_ip_address   = "0.0.0.0"
    }
  }
  tags = local.common_tags

  enable_telemetry = false
}

# AzureRM does not expose the SQL free-offer properties. Azure only accepts
# useFreeLimit when the database is created, so AzAPI creates the database.
resource "azapi_resource" "sql_database" {
  type      = "Microsoft.Sql/servers/databases@2023-08-01-preview"
  name      = "bujo"
  parent_id = module.sql_server.resource_id
  location  = var.location
  tags      = local.common_tags
  body = {
    sku = {
      name     = "GP_S_Gen5"
      tier     = "GeneralPurpose"
      family   = "Gen5"
      capacity = 2
    }
    properties = {
      maxSizeBytes                = 34359738368
      minCapacity                 = 0.5
      autoPauseDelay              = 60
      useFreeLimit                = true
      freeLimitExhaustionBehavior = "BillForUsage"
    }
  }
}
