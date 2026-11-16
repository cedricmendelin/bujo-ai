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
  mock_data "azapi_client_config" {
    defaults = {
      subscription_id = "00000000-0000-0000-0000-000000000004"
      tenant_id       = "00000000-0000-0000-0000-000000000003"
    }
  }
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
      client_id = "00000000-0000-0000-0000-000000000008"
    }
  }
  mock_data "azuread_service_principal" {
    defaults = {
      object_id = "00000000-0000-0000-0000-000000000006"
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
  auth_allowed_principal_object_ids = ""
  budget_alert_email                = "test@example.com"
  bootstrap_only                    = true
  container_image                   = "registry.example/legacy/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  deployment_client_id              = "00000000-0000-0000-0000-000000000007"
}

override_resource {
  target = module.log_analytics.azurerm_log_analytics_workspace.this
  values = {
    id           = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.OperationalInsights/workspaces/test-law"
    workspace_id = "00000000-0000-0000-0000-000000000013"
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
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.Sql/servers/test-sql/databases/bujo"
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
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.App/containerApps/ca-test1234"
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
    exists = true
    output = {
      properties = {
        template = {
          revisionSuffix = "existing"
        }
      }
    }
  }
}

override_data {
  target = module.container_app.data.azapi_resource.rg
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg"
  }
}

run "baseline_full_plan" {
  command = plan

  variables {
    bootstrap_only = true
  }

  assert {
    condition     = local.auth_allowed_principals == ""
    error_message = "An empty bootstrap allowlist must emit an empty deny-all value."
  }
}

run "baseline_full_apply" {
  command = apply

  override_resource {
    target = azuread_application.api
    values = {
      client_id = "00000000-0000-0000-0000-000000000031"
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

  variables {
    bootstrap_only = true
  }

  assert {
    condition     = local.auth_allowed_principals == ""
    error_message = "An empty bootstrap allowlist must emit an empty deny-all value."
  }
}

run "baseline_infrastructure_plan" {
  command = plan

  assert {
    condition     = local.auth_allowed_principals == ""
    error_message = "An empty bootstrap allowlist must emit an empty deny-all value."
  }
}

run "baseline_allowlist_preparation_plan" {
  command = plan

  variables {
    auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
    bootstrap_only                    = true
  }
}

run "baseline_allowlist_preparation_apply" {
  command = apply

  variables {
    auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
    bootstrap_only                    = true
  }
}

run "first_release_migration_plan" {
  command = plan

  variables {
    auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
    bootstrap_only                    = false
    container_image                   = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }
}

run "first_release_migration_apply" {
  command = apply

  variables {
    auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
    bootstrap_only                    = false
    container_image                   = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }
}

run "first_release_promotion_plan" {
  command = plan

  variables {
    auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
    bootstrap_only                    = false
    container_image                   = "ghcr.io/cedricmendelin/bujo-ai@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  }

}

run "same_principal_owner_plan" {
  command = plan

  variables {
    deployment_client_id = "00000000-0000-0000-0000-000000000008"
  }
}
