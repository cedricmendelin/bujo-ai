resource "azuread_application" "pwa_client" {
  display_name            = local.pwa_client_application_name
  description             = "Bujo-AI PWA backend-for-frontend client"
  prevent_duplicate_names = true
  sign_in_audience        = "AzureADMyOrg"
  owners                  = [local.api_owner_object_id]

  web {
    redirect_uris = [
      "https://${local.container_app_name}.${module.container_app_environment.default_domain}/signin-oidc",
      "https://localhost:7168/signin-oidc",
    ]

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  required_resource_access {
    resource_app_id = azuread_application.api.client_id

    resource_access {
      id   = random_uuid.api_scope.result
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "pwa_client" {
  client_id = azuread_application.pwa_client.client_id
  owners    = [local.api_owner_object_id]
}

resource "azuread_application_federated_identity_credential" "pwa_runtime" {
  application_id = azuread_application.pwa_client.id
  display_name   = "bujo-pwa-runtime"
  audiences      = ["api://AzureADTokenExchange"]
  issuer         = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"
  subject        = module.identity.principal_id
}
