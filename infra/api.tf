resource "random_uuid" "api_scope" {}
resource "random_uuid" "api_user_role" {}

resource "azuread_application" "api" {
  display_name            = local.api_application_name
  description             = "Bujo-AI authenticated journal API"
  prevent_duplicate_names = true
  sign_in_audience        = "AzureADMyOrg"
  identifier_uris         = [local.api_identifier_uri]
  owners                  = [data.azuread_client_config.current.object_id]

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      admin_consent_description  = "Allow the application to access the Bujo-AI journal API as the signed-in user."
      admin_consent_display_name = "Access Bujo-AI journal"
      enabled                    = true
      id                         = random_uuid.api_scope.result
      type                       = "User"
      user_consent_description   = "Allow this application to access your Bujo-AI journal."
      user_consent_display_name  = "Access your Bujo-AI journal"
      value                      = "journal.access"
    }
  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Allows an assigned user to obtain delegated tokens for the Bujo-AI journal API."
    display_name         = "Journal user"
    enabled              = true
    id                   = random_uuid.api_user_role.result
    value                = "JournalUser"
  }
}

resource "azuread_service_principal" "api" {
  client_id                    = azuread_application.api.client_id
  app_role_assignment_required = true
  owners                       = [data.azuread_client_config.current.object_id]
}
