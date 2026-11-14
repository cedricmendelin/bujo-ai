module "log_analytics" {
  source  = "Azure/avm-res-operationalinsights-workspace/azurerm"
  version = "0.5.1"

  name                                                 = local.log_analytics_name
  location                                             = var.location
  resource_group_name                                  = data.azurerm_resource_group.prod.name
  log_analytics_workspace_daily_quota_gb               = 0.1
  log_analytics_workspace_retention_in_days            = 30
  log_analytics_workspace_sku                          = "PerGB2018"
  log_analytics_workspace_local_authentication_enabled = false
  tags                                                 = local.common_tags

  enable_telemetry = false
}
