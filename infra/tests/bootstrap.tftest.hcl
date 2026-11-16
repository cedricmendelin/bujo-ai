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
mock_provider "azapi" {}
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
  auth_allowed_principal_object_ids = ""
  budget_alert_email                = "test@example.com"
  container_image                   = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}

run "deployment_requires_allowlist" {
  command = plan

  override_resource {
    target = module.container_app_environment.azapi_resource.this
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/test"
    }
  }

  override_data {
    target = module.container_app.data.azapi_resource.rg
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg"
    }
  }

  expect_failures = [var.auth_allowed_principal_object_ids]
}

run "migration_job_uses_application_image" {
  command = plan

  variables {
    auth_allowed_principal_object_ids = "00000000-0000-0000-0000-000000000002"
    container_image                   = "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }

  assert {
    condition     = azurerm_container_app_job.migration.template[0].container[0].image == "ghcr.io/cedricmendelin/bujo-ai@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" && azurerm_container_app_job.migration.template[0].container[0].command[0] == "dotnet" && azurerm_container_app_job.migration.template[0].container[0].args[0] == "--migrate-only"
    error_message = "Normal deployment must use the application migration image and command."
  }
}

run "bootstrap_job_is_inert" {
  command = plan

  variables {
    bootstrap_only = true
  }

  plan_options {
    target = [azurerm_container_app_job.migration]
  }

  assert {
    condition     = azurerm_container_app_job.migration.template[0].container[0].image == "mcr.microsoft.com/k8se/quickstart-jobs@sha256:912ce4be4e9c4eb75cafa3f846d47d8d4ccebde1135c3059d1490d0b8c457187"
    error_message = "Bootstrap must use the pinned public placeholder image."
  }

  assert {
    condition     = length(azurerm_container_app_job.migration.template[0].container[0].command) == 1 && azurerm_container_app_job.migration.template[0].container[0].command[0] == "/bootstrap-disabled" && length(azurerm_container_app_job.migration.template[0].container[0].args) == 0 && length(azurerm_container_app_job.migration.template[0].container[0].env) == 0
    error_message = "The bootstrap job must not contain a migration command or database configuration."
  }

  assert {
    condition     = length(azurerm_container_app_job.migration.manual_trigger_config) == 1
    error_message = "The bootstrap job must be manual, with no schedule or event trigger."
  }
}
