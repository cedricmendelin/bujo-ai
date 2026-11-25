location       = "switzerlandnorth"
name_suffix    = "bujo001"
key_vault_name = "bujo-ai-prod-kv"
budget_amount  = 80

foundry_enabled               = true
foundry_runtime_roles_enabled = false
foundry_location              = "switzerlandnorth"
foundry_access_group          = null
foundry_deployments = {
  chat = {
    model    = "gpt-6.1-terra"
    version  = "2026-10-26"
    sku      = "DataZoneStandard"
    capacity = 1
  }
  sol = {
    model    = "gpt-6.2-sol"
    version  = "2026-11-17"
    sku      = "DataZoneStandard"
    capacity = 1
  }
  astra = {
    model    = "gpt-6.2-astra"
    version  = "2026-11-17"
    sku      = "DataZoneStandard"
    capacity = 1
  }
  embedding = {
    model    = "text-embedding-3-large"
    version  = "1"
    sku      = "Standard"
    capacity = 1
  }
}
