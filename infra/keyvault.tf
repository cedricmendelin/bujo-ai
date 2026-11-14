module "key_vault" {
  source  = "Azure/avm-res-keyvault-vault/azurerm"
  version = "0.13.1"

  name                          = local.key_vault_name
  location                      = var.location
  parent_id                     = data.azurerm_resource_group.prod.id
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  purge_protection_enabled      = true
  public_network_access_enabled = true
  role_assignments = {
    container_app_secrets_user = {
      role_definition_id_or_name = "Key Vault Secrets User"
      principal_id               = module.identity.principal_id
    }
  }
  tags = local.common_tags

  enable_telemetry = false
}
