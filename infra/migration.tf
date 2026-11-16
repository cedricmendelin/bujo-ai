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
      image   = var.bootstrap_only ? "mcr.microsoft.com/k8se/quickstart-jobs@sha256:912ce4be4e9c4eb75cafa3f846d47d8d4ccebde1135c3059d1490d0b8c457187" : var.container_image
      command = var.bootstrap_only ? ["/bootstrap-disabled"] : ["dotnet", "Bujo.Host.dll"]
      args    = var.bootstrap_only ? [] : ["--migrate-only"]
      cpu     = 0.25
      memory  = "0.5Gi"

      dynamic "env" {
        for_each = var.bootstrap_only ? {} : {
          DOTNET_ENVIRONMENT         = "Production"
          ConnectionStrings__journal = local.migration_sql_connection_string
        }
        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }

  tags = local.common_tags
}
