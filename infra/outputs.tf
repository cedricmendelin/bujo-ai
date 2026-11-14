output "container_app_url" {
  description = "Public HTTPS URL for the container app."
  value       = module.container_app.fqdn_url
}

output "key_vault_uri" {
  description = "URI of the production Key Vault."
  value       = module.key_vault.uri
}

output "sql_database_id" {
  description = "Resource ID of the production SQL database."
  value       = azurerm_mssql_database.bujo.id
}
