mock_provider "azurerm" {
  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id = "00000000-0000-0000-0000-000000000003"
    }
  }
  mock_data "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg"
    }
  }
}
mock_provider "azapi" {
  # Stable subscription so module-derived parent IDs do not drift between runs.
  mock_data "azapi_client_config" {
    defaults = {
      subscription_id = "00000000-0000-0000-0000-000000000004"
      tenant_id       = "00000000-0000-0000-0000-000000000003"
    }
  }
  # The environment module reads the workspace key through an ephemeral action.
  mock_ephemeral "azapi_resource_action" {
    defaults = {
      output = { primarySharedKey = "mock-shared-key" }
    }
  }
}
mock_provider "azuread" {
  mock_data "azuread_client_config" {
    defaults = {
      object_id = "00000000-0000-0000-0000-000000000005"
    }
  }
}
mock_provider "random" {}
mock_provider "time" {}
mock_provider "modtm" {}

variables {
  resource_group_name               = "test-rg"
  name_suffix                       = "test1234"
  key_vault_name                    = "test-keyvault"
  sql_entra_admin_login             = "test-admin"
  sql_entra_admin_object_id         = "00000000-0000-0000-0000-000000000001"
  auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
  budget_alert_email                = "test@example.com"
  container_image                   = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}

# Simulated release sequence on mock providers (no Azure). Each run's JSON plan
# is exposed through `terraform test -json -verbose` for the gate validators in
# tests/infrastructure/test_release_plans.py.
#
# Release A is the previous fully deployed release; release B is the next one.

override_resource {
  target = azuread_application.api
  values = {
    client_id = "00000000-0000-0000-0000-000000000031"
  }
}

override_resource {
  target = azuread_application.pwa_client
  values = {
    id        = "/applications/00000000-0000-0000-0000-000000000033"
    client_id = "00000000-0000-0000-0000-000000000032"
  }
}

override_resource {
  target = random_uuid.api_scope
  values = {
    result = "00000000-0000-0000-0000-000000000011"
    id     = "00000000-0000-0000-0000-000000000011"
  }
}

override_resource {
  target = random_uuid.api_user_role
  values = {
    result = "00000000-0000-0000-0000-000000000012"
    id     = "00000000-0000-0000-0000-000000000012"
  }
}

override_resource {
  target = module.log_analytics.azurerm_log_analytics_workspace.this
  values = {
    id           = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.OperationalInsights/workspaces/test-law"
    workspace_id = "00000000-0000-0000-0000-000000000013"
  }
}

override_resource {
  target = module.application_insights.azapi_resource.this
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.Insights/components/test-appi"
    output = {
      properties = {
        AppId              = "mock-app-id"
        ConnectionString   = "mock-connection-string"
        InstrumentationKey = "mock-instrumentation-key"
      }
    }
  }
}

override_resource {
  target = azurerm_container_app_job.migration
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.App/jobs/test-migration"
  }
}

override_data {
  target = module.container_app_environment.data.azapi_resource.customer_id
  values = {
    output = { properties = { customerId = "00000000-0000-0000-0000-000000000013" } }
  }
}

override_resource {
  target = module.sql_server.azurerm_mssql_server.this
  values = {
    id                          = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.Sql/servers/test-sql"
    fully_qualified_domain_name = "test-sql.database.windows.net"
  }
}

override_resource {
  target = azapi_resource.sql_database
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.Sql/servers/test-sql/databases/test-db"
  }
}

override_resource {
  target = module.key_vault.azapi_resource.this
  values = {
    id     = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.KeyVault/vaults/test-keyvault"
    output = { properties = { vaultUri = "https://test-keyvault.vault.azure.net/" } }
  }
}

override_resource {
  target = module.identity.azurerm_user_assigned_identity.this
  values = {
    id           = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-runtime"
    client_id    = "00000000-0000-0000-0000-000000000021"
    principal_id = "00000000-0000-0000-0000-000000000022"
  }
}

override_resource {
  target = module.migration_identity.azurerm_user_assigned_identity.this
  values = {
    id           = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-migration"
    client_id    = "00000000-0000-0000-0000-000000000023"
    principal_id = "00000000-0000-0000-0000-000000000024"
  }
}

override_resource {
  target = module.container_app_environment.azapi_resource.this
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/test"
    output = {
      properties = {
        defaultDomain = "test.switzerlandnorth.azurecontainerapps.io"
        staticIp      = "192.0.2.10"
      }
    }
  }
}

override_resource {
  target = module.container_app.azapi_resource.container_app
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.App/containerApps/test-app"
    output = {
      properties = {
        customDomainVerificationId = "test-verification"
        latestReadyRevisionName    = "test-app--0000001"
        latestRevisionName         = "test-app--0000001"
        latestRevisionFqdn         = "test-app--0000001.test.switzerlandnorth.azurecontainerapps.io"
        outboundIpAddresses        = ["192.0.2.20"]
        configuration = {
          ingress = { fqdn = "test-app.test.switzerlandnorth.azurecontainerapps.io" }
        }
      }
    }
  }
}

override_data {
  target = module.container_app.data.azapi_resource.existing
  values = {
    exists = false
  }
}

override_data {
  target = module.container_app.data.azapi_resource.rg
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg"
  }
}

run "release_baseline_apply" {
  command = apply

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }
}

run "release_migration_plan" {
  command = plan

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }

  assert {
    condition     = azurerm_container_app_job.migration.template[0].container[0].image == "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    error_message = "The targeted migration plan must move the job to release B."
  }
}

run "release_migration_apply" {
  command = apply

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }
}

run "release_migration_rerun_plan" {
  command = plan

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }
}

run "infrastructure_retains_both_images" {
  command = plan

  variables {
    retained_app_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    container_image    = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }
}

run "release_promotion_plan" {
  command = plan

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

  assert {
    condition     = azurerm_container_app_job.migration.template[0].container[0].image == "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    error_message = "Promotion must keep the migrated job image."
  }
}

# First release after only the approved stage-one bootstrap (targeted inert
# job and its dependencies), in separate mocked state. The promotion plan
# exposes the first-release baseline gap: everything else is still absent.

run "first_release_bootstrap_apply" {
  command   = apply
  state_key = "first_release"

  variables {
    bootstrap_only = true
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }

  assert {
    condition     = startswith(output.migration_sql_connection_string, "Server=tcp:") && output.migration_job_id != null
    error_message = "Stage one must record the trusted migration SQL target and job ID the gate reads."
  }
}

run "first_release_migration_plan" {
  command   = plan
  state_key = "first_release"

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }
}

run "first_release_migration_apply" {
  command   = apply
  state_key = "first_release"

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }
}

run "first_release_promotion_plan" {
  command   = plan
  state_key = "first_release"

  variables {
    container_image = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }
}
