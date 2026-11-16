resource "azurerm_container_app_job" "migration" {
  name                         = local.migration_job_name
  location                     = var.location
  resource_group_name          = data.azurerm_resource_group.prod.name
  container_app_environment_id = module.container_app_environment.resource_id

  replica_timeout_in_seconds = 1800
  replica_retry_limit        = 0

  depends_on = [
    module.identity,
    module.sql_server,
    azapi_resource.sql_database
  ]

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [module.migration_identity.resource_id]
  }

  template {
    container {
      name    = "bujo-migrate"
      image   = var.container_image
      command = ["dotnet", "Bujo.Host.dll"]
      args    = ["--migrate-only"]
      cpu     = 0.25
      memory  = "0.5Gi"

      env {
        name  = "DOTNET_ENVIRONMENT"
        value = "Production"
      }

      env {
        name  = "ConnectionStrings__journal"
        value = local.migration_sql_connection_string
      }
    }
  }

  tags = local.common_tags
}
