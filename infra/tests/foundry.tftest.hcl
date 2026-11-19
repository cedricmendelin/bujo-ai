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

override_data {
  target = module.container_app.data.azapi_resource.rg
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg"
  }
}

run "foundry_disabled" {
  command = plan

  assert {
    condition = (
      length(azurerm_cognitive_account.foundry) == 0
      && length(azurerm_cognitive_account_project.foundry) == 0
      && length(azurerm_cognitive_deployment.model) == 0
      && length(azurerm_role_assignment.foundry_group) == 0
    )
    error_message = "The default-disabled Foundry configuration must plan no Foundry resources."
  }
}

run "foundry_enabled" {
  command = plan

  variables {
    foundry_enabled = true
  }

  assert {
    condition     = length(azurerm_role_assignment.foundry_group) == 0
    error_message = "An unset group must not create role assignments, even when Foundry is enabled."
  }

  assert {
    condition     = length(azurerm_cognitive_account.foundry) == 1
    error_message = "Enabling Foundry must create exactly one Azure AI Services account."
  }

  assert {
    condition = (
      azurerm_cognitive_account.foundry[0].kind == "AIServices"
      && azurerm_cognitive_account.foundry[0].local_auth_enabled == false
      && azurerm_cognitive_account.foundry[0].project_management_enabled
      && azurerm_cognitive_account.foundry[0].public_network_access_enabled
    )
    error_message = "The Foundry account must use keyless auth and support one public-network project."
  }

  assert {
    condition     = length(azurerm_cognitive_account_project.foundry) == 1
    error_message = "Enabling Foundry must create exactly one project."
  }

  assert {
    condition = toset(keys(azurerm_cognitive_deployment.model)) == toset([
      "chat",
      "sol",
      "astra",
      "embedding",
    ])
    error_message = "Enabling Foundry must create only the chat, Sol, Astra, and embedding deployments."
  }

  assert {
    condition = alltrue([
      for deployment in values(azurerm_cognitive_deployment.model) :
      deployment.sku[0].capacity == 1
    ])
    error_message = "The proposed deployments must use low-capacity pay-per-token SKUs."
  }

  assert {
    condition = (
      azurerm_cognitive_deployment.model["chat"].sku[0].name == "DataZoneStandard"
      && azurerm_cognitive_deployment.model["sol"].sku[0].name == "DataZoneStandard"
      && azurerm_cognitive_deployment.model["astra"].sku[0].name == "DataZoneStandard"
      && azurerm_cognitive_deployment.model["embedding"].sku[0].name == "Standard"
    )
    error_message = "The GPT deployments must use Data Zone Standard and the embedding deployment must use Standard."
  }

  assert {
    condition     = length(regexall("(?mi)^output\\s+\"[^\"]*(foundry|cognitive|model)[^\"]*\"", file("${path.root}/outputs.tf"))) == 0
    error_message = "Foundry model endpoints, names, and credentials must not be added as Terraform outputs."
  }
}

run "foundry_disabled_with_group" {
  command = plan

  variables {
    foundry_access_group = {
      object_id    = "00000000-0000-0000-0000-000000000009"
      display_name = "test-foundry-users"
    }
  }

  assert {
    condition     = length(azurerm_role_assignment.foundry_group) == 0
    error_message = "A configured group must not create role assignments while Foundry is disabled."
  }
}

run "foundry_enabled_with_group" {
  command = plan

  variables {
    foundry_enabled = true
    foundry_access_group = {
      object_id    = "00000000-0000-0000-0000-000000000009"
      display_name = "test-foundry-users"
    }
  }

  override_resource {
    target          = azurerm_cognitive_account.foundry[0]
    override_during = plan
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000004/resourceGroups/test-rg/providers/Microsoft.CognitiveServices/accounts/test-foundry"
    }
  }

  assert {
    condition = toset(keys(azurerm_role_assignment.foundry_group)) == toset([
      "Cognitive Services OpenAI User",
      "Foundry User",
    ])
    error_message = "Only the two approved non-management group roles may be assigned."
  }

  assert {
    condition = alltrue([
      for role, assignment in azurerm_role_assignment.foundry_group :
      assignment.scope == azurerm_cognitive_account.foundry[0].id
      && assignment.role_definition_name == role
      && assignment.principal_type == "Group"
      && assignment.principal_id == var.foundry_access_group.object_id
      && assignment.principal_id != var.deployment_client_id
      && assignment.principal_id != data.azuread_service_principal.deployment[0].object_id
      && assignment.name == uuidv5("url", "${lower(azurerm_cognitive_account.foundry[0].id)}/${lower(var.foundry_access_group.object_id)}/${role}")
    ])
    error_message = "Roles must have stable names, target only the configured group, and use the Foundry account scope, never the deployment identity."
  }

  assert {
    condition     = length(regexall("(?mi)^\\s*(resource|data)\\s+\"azuread_group", join("\n", [for name in fileset(path.root, "*.tf") : file("${path.root}/${name}")]))) == 0
    error_message = "Foundry access must not create, manage membership of, or look up Entra groups."
  }
}

run "foundry_invalid_group_guid_rejected" {
  command = plan

  variables {
    foundry_access_group = {
      object_id    = "not-a-guid"
      display_name = "test-foundry-users"
    }
  }

  expect_failures = [var.foundry_access_group]
}

run "foundry_blank_group_name_rejected" {
  command = plan

  variables {
    foundry_access_group = {
      object_id    = "00000000-0000-0000-0000-000000000009"
      display_name = "   "
    }
  }

  expect_failures = [var.foundry_access_group]
}

run "foundry_empty_deployments_rejected" {
  command = plan

  variables {
    foundry_deployments = {}
  }

  expect_failures = [var.foundry_deployments]
}

run "foundry_invalid_sku_rejected" {
  command = plan

  variables {
    foundry_deployments = {
      invalid = {
        model    = "model"
        version  = "1"
        sku      = "ProvisionedManaged"
        capacity = 1
      }
    }
  }

  expect_failures = [var.foundry_deployments]
}
