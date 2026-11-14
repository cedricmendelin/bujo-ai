module "key_vault" {
  source  = "Azure/avm-res-keyvault-vault/azurerm"
  version = "0.13.1"

  name                          = local.key_vault_name
  location                      = var.location
  parent_id                     = data.azurerm_resource_group.prod.id
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  soft_delete_retention_days    = 7
  purge_protection_enabled      = true
  public_network_access_enabled = true
  tags                          = local.common_tags

  enable_telemetry = false
}
