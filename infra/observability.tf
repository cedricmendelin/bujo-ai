module "log_analytics" {
  source  = "Azure/avm-res-operationalinsights-workspace/azurerm"
  version = "0.5.1"

  name                                               = local.log_analytics_name
  location                                           = var.location
  resource_group_name                                = data.azurerm_resource_group.prod.name
  log_analytics_workspace_daily_quota_gb             = 0.1
  log_analytics_workspace_internet_ingestion_enabled = "true"
  log_analytics_workspace_internet_query_enabled     = "true"
  log_analytics_workspace_retention_in_days          = 30
  log_analytics_workspace_sku                        = "PerGB2018"
  tags                                               = local.common_tags

  enable_telemetry = false
}

module "application_insights" {
  source  = "Azure/avm-res-insights-component/azurerm"
  version = "0.5.0"

  name             = local.application_insights_name
  location         = var.location
  parent_id        = data.azurerm_resource_group.prod.id
  application_type = "web"

  # AVM expects the Log Analytics ARM resource ID, not its customer GUID.
  workspace_id = module.log_analytics.resource_id

  retention_in_days                     = 30
  daily_data_cap_in_gb                  = 0.1
  daily_data_cap_notifications_disabled = false
  sampling_percentage                   = 100
  tags                                  = local.common_tags

  enable_telemetry = false
}
