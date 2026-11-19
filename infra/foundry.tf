resource "azurerm_cognitive_account" "foundry" {
  count = var.foundry_enabled ? 1 : 0

  name                          = local.foundry_account_name
  location                      = var.foundry_location
  resource_group_name           = data.azurerm_resource_group.prod.name
  kind                          = "AIServices"
  sku_name                      = "S0"
  custom_subdomain_name         = local.foundry_account_name
  local_auth_enabled            = false
  project_management_enabled    = true
  public_network_access_enabled = true

  identity {
    type = "SystemAssigned"
  }

  tags = local.common_tags
}

resource "azurerm_cognitive_account_project" "foundry" {
  count = var.foundry_enabled ? 1 : 0

  name                 = local.foundry_project_name
  cognitive_account_id = azurerm_cognitive_account.foundry[0].id
  location             = var.foundry_location
  description          = "Bujo-AI optional model deployments"

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_role_assignment" "foundry_group" {
  for_each = var.foundry_enabled && var.foundry_access_group != null ? toset([
    "Cognitive Services OpenAI User",
    "Foundry User",
  ]) : toset([])

  name                 = uuidv5("url", "${lower(azurerm_cognitive_account.foundry[0].id)}/${lower(var.foundry_access_group.object_id)}/${each.key}")
  scope                = azurerm_cognitive_account.foundry[0].id
  role_definition_name = each.key
  principal_id         = var.foundry_access_group.object_id
  principal_type       = "Group"
}

resource "azurerm_role_assignment" "foundry_runtime" {
  for_each = var.foundry_enabled && var.foundry_runtime_roles_enabled ? toset([
    "Cognitive Services OpenAI User",
    "Cognitive Services Speech User",
  ]) : toset([])

  name                 = uuidv5("url", "${lower(azurerm_cognitive_account.foundry[0].id)}/${lower(module.identity.principal_id)}/${lower(each.key)}")
  scope                = azurerm_cognitive_account.foundry[0].id
  role_definition_name = each.key
  principal_id         = module.identity.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_cognitive_deployment" "model" {
  for_each = var.foundry_enabled ? var.foundry_deployments : {}

  name                 = each.key
  cognitive_account_id = azurerm_cognitive_account.foundry[0].id

  model {
    format  = "OpenAI"
    name    = each.value.model
    version = each.value.version
  }

  sku {
    name     = each.value.sku
    capacity = each.value.capacity
  }

  depends_on = [azurerm_cognitive_account_project.foundry]
}
