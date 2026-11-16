resource "azuread_application" "pwa_client" {
  display_name            = local.pwa_client_application_name
  description             = "Bujo-AI PWA backend-for-frontend client"
  prevent_duplicate_names = true
  sign_in_audience        = "AzureADMyOrg"
  owners                  = [local.api_owner_object_id]

  web {
    redirect_uris = ["${module.container_app.fqdn_url}/signin-oidc"]

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
