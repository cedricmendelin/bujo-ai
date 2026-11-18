location       = "switzerlandnorth"
name_suffix    = "bujo001"
key_vault_name = "bujo-ai-prod-kv"
budget_amount  = 50

foundry_enabled  = false
foundry_location = "switzerlandnorth"
foundry_access_group = {
  object_id    = "42c2dcb8-689b-4c8a-b711-59b44a4f4e03"
  display_name = "bujo-ai-foundry-admins"
}
foundry_deployments = {
  chat = {
    model    = "gpt-6.1-terra"
    version  = "2026-10-26"
    sku      = "DataZoneStandard"
    capacity = 1
  }
  decision = {
    model    = "gpt-5-mini"
    version  = "2025-08-07"
    sku      = "DataZoneStandard"
    capacity = 1
  }
  embedding = {
    model    = "text-embedding-3-small"
    version  = "1"
    sku      = "DataZoneStandard"
    capacity = 1
  }
}
