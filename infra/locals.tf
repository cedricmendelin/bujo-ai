locals {
  common_tags = {
    application = "bujo-ai"
    environment = "prod"
    managed_by  = "terraform"
  }

  identity_name      = "id-${var.name_suffix}"
  log_analytics_name = "law-${var.name_suffix}"
  environment_name   = "cae-${var.name_suffix}"
  container_app_name = "ca-${var.name_suffix}"
  key_vault_name     = var.key_vault_name
  sql_server_name    = "sql-${var.name_suffix}"
}
