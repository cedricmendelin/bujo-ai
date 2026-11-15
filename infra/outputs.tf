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
