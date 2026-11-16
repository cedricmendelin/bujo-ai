locals {
  common_tags = {
    application = "bujo-ai"
    environment = "prod"
    managed_by  = "terraform"
  }

  identity_name               = "id-${var.name_suffix}"
  migration_identity_name     = "id-${var.name_suffix}-migrate"
  log_analytics_name          = "law-${var.name_suffix}"
  environment_name            = "cae-${var.name_suffix}"
  container_app_name          = "ca-${var.name_suffix}"
  migration_job_name          = "job-${var.name_suffix}-migrate"
  api_application_name        = "app-${var.name_suffix}-api"
  pwa_client_application_name = "app-${var.name_suffix}-pwa"
  api_identifier_uri          = "api://bujo-ai-${var.name_suffix}"
  key_vault_name              = var.key_vault_name
  sql_server_name             = "sql-${var.name_suffix}"
  sql_server_fqdn             = "${local.sql_server_name}.database.windows.net"

  runtime_sql_connection_string   = "Server=tcp:${local.sql_server_fqdn},1433;Initial Catalog=bujo;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id=${module.identity.client_id}"
  migration_sql_connection_string = "Server=tcp:${local.sql_server_fqdn},1433;Initial Catalog=bujo;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id=${module.migration_identity.client_id}"
  deployment_uses_current_identity = (
    var.deployment_client_id == ""
    || lower(var.deployment_client_id) == lower(data.azuread_client_config.current.client_id)
  )
  api_owner_object_id = local.deployment_uses_current_identity ? data.azuread_client_config.current.object_id : data.azuread_service_principal.deployment[0].object_id
  auth_allowed_principals = trimspace(var.auth_allowed_principal_object_ids) == "" ? "" : join(",", [
    for object_id in split(",", var.auth_allowed_principal_object_ids) :
    "${data.azurerm_client_config.current.tenant_id}:${trimspace(object_id)}"
  ])
}
