module "container_app_environment" {
  source  = "Azure/avm-res-app-managedenvironment/azurerm"
  version = "0.6.0"

  name                = local.environment_name
  location            = var.location
  resource_group_name = data.azurerm_resource_group.prod.name
  log_analytics_workspace = {
    resource_id = module.log_analytics.resource_id
  }
  tags = local.common_tags

  enable_telemetry = false
}

module "container_app" {
  source  = "Azure/avm-res-app-containerapp/azurerm"
  version = "0.9.0"

  name                                  = local.container_app_name
  location                              = var.location
  resource_group_name                   = data.azurerm_resource_group.prod.name
  container_app_environment_resource_id = module.container_app_environment.resource_id
  managed_identities = {
    system_assigned            = false
    user_assigned_resource_ids = [module.identity.resource_id]
  }
  revision_mode = "Single"
  template = {
    min_replicas = 0
    max_replicas = 1
    containers = [
      {
        name   = "bujo-ai"
        image  = var.container_image
        cpu    = 0.25
        memory = "0.5Gi"
      }
    ]
  }
  ingress = {
    external_enabled = true
    target_port      = 80
    transport        = "auto"
    traffic_weight = [
      {
        percentage      = 100
        latest_revision = true
      }
    ]
  }
  tags = local.common_tags

  enable_telemetry = false
}
