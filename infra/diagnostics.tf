# Container Apps console and system logs are already routed by the managed
# environment's Log Analytics configuration in containerapp.tf. Do not create
# a second setting for those categories; that would duplicate platform logs.
# Use the discovered supported metrics categories only for the app, job, and
# environment; do not enable environment HTTP/Spring/session log categories.
resource "azurerm_monitor_diagnostic_setting" "container_app" {
  name                           = "container-app-metrics-to-law"
  target_resource_id             = module.container_app.resource_id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_metric {
    category = "AllMetrics"
  }
}

resource "azurerm_monitor_diagnostic_setting" "migration_job" {
  name                           = "migration-job-metrics-to-law"
  target_resource_id             = azurerm_container_app_job.migration.id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  # Read-only Azure category discovery reported Basic for this job; do not
  # substitute the AllMetrics category used by other resource types.
  enabled_metric {
    category = "Basic"
  }
}

resource "azurerm_monitor_diagnostic_setting" "container_app_environment" {
  name                           = "container-app-environment-metrics-to-law"
  target_resource_id             = module.container_app_environment.resource_id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_metric {
    category = "AllMetrics"
  }
}

# Key Vault's documented AuditEvent category records access/control events,
# not secret values. Avoid category groups and policy-evaluation details.
# https://learn.microsoft.com/azure/azure-monitor/reference/supported-logs/microsoft-keyvault-vaults-logs
# https://learn.microsoft.com/azure/azure-monitor/reference/supported-metrics/microsoft-keyvault-vaults-metrics
resource "azurerm_monitor_diagnostic_setting" "key_vault" {
  name                           = "key-vault-to-law"
  target_resource_id             = module.key_vault.resource_id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_log {
    category = "AuditEvent"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

# The SQL server supports AllMetrics. Read-only discovery confirmed that the
# deployed bujo database supports Basic, InstanceAndAppAdvanced, and
# WorkloadManagement metric categories. Select Basic only; keep all database
# log categories disabled because they can include statement/query/error data.
# https://learn.microsoft.com/azure/azure-monitor/reference/supported-logs/microsoft-sql-servers-databases-logs
# https://learn.microsoft.com/azure/azure-monitor/reference/supported-metrics/microsoft-sql-servers-databases-metrics
resource "azurerm_monitor_diagnostic_setting" "sql_server" {
  name                           = "sql-server-metrics-to-law"
  target_resource_id             = module.sql_server.resource_id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_metric {
    category = "AllMetrics"
  }
}

resource "azurerm_monitor_diagnostic_setting" "sql_database" {
  name                           = "sql-database-metrics-to-law"
  target_resource_id             = azapi_resource.sql_database.id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_metric {
    category = "Basic"
  }
}

# The account and project exist only when Foundry is enabled. Export supported
# platform metrics only; category presence is not proof of safe log contents.
# Explicitly exclude Audit, RequestResponse, AzureOpenAIRequestUsage, and Trace
# resource-log categories to avoid unreviewed payload/error fields.
# The project-to-Application-Insights connection is intentionally blocked:
# Microsoft documents Foundry server-side tracing as capturing prompt content.
# The app uses a direct HttpClient model client, which that tracing does not
# instrument. Enable no Foundry project connection absent privacy verification.
# https://learn.microsoft.com/azure/foundry/observability/how-to/trace-agent-setup
# https://learn.microsoft.com/azure/azure-monitor/reference/supported-logs/microsoft-cognitiveservices-accounts-logs
# https://learn.microsoft.com/azure/azure-monitor/reference/supported-metrics/microsoft-cognitiveservices-accounts-metrics
resource "azurerm_monitor_diagnostic_setting" "foundry_account" {
  count = var.foundry_enabled ? 1 : 0

  name                           = "foundry-account-metrics-to-law"
  target_resource_id             = azurerm_cognitive_account.foundry[0].id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_metric {
    category = "AllMetrics"
  }
}

# The Foundry project also exposes AllMetrics. Keep its Audit and Trace logs
# disabled until their contents have been reviewed.
resource "azurerm_monitor_diagnostic_setting" "foundry_project" {
  count = var.foundry_enabled ? 1 : 0

  name                           = "foundry-project-metrics-to-law"
  target_resource_id             = azurerm_cognitive_account_project.foundry[0].id
  log_analytics_workspace_id     = module.log_analytics.resource_id
  log_analytics_destination_type = "AzureDiagnostics"

  enabled_metric {
    category = "AllMetrics"
  }
}

# Log Analytics exposes Audit, SummaryLogs, Jobs, and AllMetrics categories,
# but its log payloads were not reviewed. No self-diagnostic setting is added.
# Read-only discovery returned an error for managed-identity categories, so
# support remains unverified.
# Tenant sign-in and audit exports are outside the approved scope.
