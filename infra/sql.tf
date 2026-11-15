module "sql_server" {
  source  = "Azure/avm-res-sql-server/azurerm"
  version = "0.2.1"

  name                = local.sql_server_name
  location            = var.location
  resource_group_name = data.azurerm_resource_group.prod.name
  server_version      = "12.0"
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

# AzureRM does not yet expose the SQL free-limit properties, so AzAPI sets them after the database is created.
resource "azurerm_mssql_database" "bujo" {
  name                        = "bujo"
  server_id                   = module.sql_server.resource_id
  sku_name                    = "GP_S_Gen5_2"
  max_size_gb                 = 32
  min_capacity                = 0.5
  auto_pause_delay_in_minutes = 60
}

resource "azapi_update_resource" "sql_free_offer" {
  type        = "Microsoft.Sql/servers/databases@2023-08-01-preview"
  resource_id = azurerm_mssql_database.bujo.id
  body = {
    properties = {
      useFreeLimit                = true
      freeLimitExhaustionBehavior = "AutoPause"
    }
  }

  depends_on = [azurerm_mssql_database.bujo]
}
