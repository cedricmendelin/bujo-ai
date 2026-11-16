output "container_app_url" {
  description = "Public HTTPS URL for the container app."
  value       = module.container_app.fqdn_url
}

output "key_vault_uri" {
  description = "URI of the production Key Vault."
  value       = module.key_vault.uri
}

output "key_vault_id" {
  description = "Resource ID of the production Key Vault."
  value       = module.key_vault.resource_id
}

output "runtime_identity_principal_id" {
  description = "Principal ID of the runtime user-assigned managed identity."
  value       = module.identity.principal_id
}

output "runtime_identity_name" {
  description = "Name of the runtime user-assigned managed identity."
  value       = module.identity.resource_name
}

output "migration_identity_principal_id" {
  description = "Principal ID of the migration user-assigned managed identity."
  value       = module.migration_identity.principal_id
}

output "migration_identity_name" {
  description = "Name of the migration user-assigned managed identity."
  value       = module.migration_identity.resource_name
}

output "sql_server_identity_principal_id" {
  description = "Principal ID of the SQL logical server system-assigned identity."
  value       = module.sql_server.resource.identity[0].principal_id
  sensitive   = true
}

output "migration_job_id" {
  description = "Resource ID of the manually triggered database migration job."
  value       = azurerm_container_app_job.migration.id
}

output "api_application_client_id" {
  description = "Client ID and expected audience of the Bujo-AI API application."
  value       = azuread_application.api.client_id
}

output "api_application_identifier_uri" {
  description = "Identifier URI of the Bujo-AI API application."
  value       = local.api_identifier_uri
}
