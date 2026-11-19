variable "resource_group_name" {
  type        = string
  description = "Name of the owner-created production resource group."
}

variable "key_vault_name" {
  type        = string
  description = "Globally unique 3–24 character production Key Vault name."

  validation {
    condition     = can(regex("^[A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9]$", var.key_vault_name)) && !can(regex("--", var.key_vault_name))
    error_message = "key_vault_name must be 3–24 letters, numbers, or hyphens, start with a letter, and contain no consecutive hyphens."
  }
}

variable "location" {
  type        = string
  description = "Azure region for production resources."
  default     = "switzerlandnorth"
}

variable "name_suffix" {
  type        = string
  description = "Short, globally unique lowercase suffix used in Azure resource names."

  validation {
    condition     = can(regex("^[a-z0-9]{4,8}$", var.name_suffix))
    error_message = "name_suffix must contain 4 to 8 lowercase letters or digits."
  }
}

variable "container_image" {
  type        = string
  description = "Immutable GHCR release image digest; empty only for owner bootstrap."
  default     = ""

  validation {
    condition     = var.bootstrap_only || can(regex("^ghcr\\.io/cedricmendelin/bujo-ai@sha256:[0-9a-f]{64}$", var.container_image))
    error_message = "A production release must use the immutable Bujo-AI GHCR image digest."
  }
}

variable "bootstrap_only" {
  type        = bool
  description = "Use an inert placeholder for a migration job that has not received a release."
  default     = false
}

variable "retained_app_image" {
  type        = string
  description = "Existing app image to retain during infrastructure-only applies; releases use container_image instead."
  default     = null
}

variable "deployment_client_id" {
  type        = string
  description = "Optional Entra application client ID whose service principal owns the managed API registration."
  default     = ""

  validation {
    condition     = var.deployment_client_id == "" || can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.deployment_client_id))
    error_message = "deployment_client_id must be empty or a GUID."
  }
}

variable "auth_allowed_principal_object_ids" {
  type        = string
  description = "Comma-separated Entra object IDs allowed to call the API."
  sensitive   = true
  default     = ""

  validation {
    condition = var.bootstrap_only || (length(split(",", var.auth_allowed_principal_object_ids)) > 0 && alltrue([
      for object_id in split(",", var.auth_allowed_principal_object_ids) :
      can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", trimspace(object_id)))
    ]))
    error_message = "auth_allowed_principal_object_ids must be a comma-separated list of GUIDs."
  }
}

variable "sql_entra_admin_login" {
  type        = string
  description = "Display name of the Entra security group that administers Azure SQL."
}

variable "sql_entra_admin_object_id" {
  type        = string
  description = "Object ID of the Entra security group that administers Azure SQL."

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.sql_entra_admin_object_id))
    error_message = "sql_entra_admin_object_id must be a GUID."
  }
}

variable "budget_alert_email" {
  type        = string
  description = "Email address for monthly budget alerts."

  validation {
    condition     = can(regex("^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$", var.budget_alert_email))
    error_message = "budget_alert_email must be a valid email address."
  }
}

variable "budget_amount" {
  type        = number
  description = "Monthly budget amount in the subscription billing currency."
  default     = 50

  validation {
    condition     = var.budget_amount == 50
    error_message = "The production monthly budget must remain 50."
  }
}

variable "foundry_enabled" {
  type        = bool
  description = "Create the optional Foundry account, project, and model deployments."
  default     = false
}

variable "foundry_access_group" {
  type = object({
    object_id    = string
    display_name = string
  })
  description = "Existing Entra group for account-scoped Foundry access; null leaves group role assignments external. Terraform does not manage or look up the group."
  default     = null

  validation {
    condition = var.foundry_access_group == null ? true : (
      can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.foundry_access_group.object_id))
      && try(trimspace(var.foundry_access_group.display_name) != "", false)
    )
    error_message = "foundry_access_group must be null or contain a GUID object_id and a non-empty display_name."
  }
}

variable "foundry_location" {
  type        = string
  description = "Azure region for the optional Foundry account and project."
  default     = "switzerlandnorth"

  validation {
    condition     = can(regex("^[a-z0-9]+$", var.foundry_location))
    error_message = "foundry_location must be an Azure region name."
  }
}

variable "foundry_deployments" {
  type = map(object({
    model    = string
    version  = string
    sku      = string
    capacity = number
  }))
  description = "Optional Foundry model deployments, keyed by their deployment names."
  default = {
    chat = {
      model    = "gpt-6.1-terra"
      version  = "2026-10-26"
      sku      = "DataZoneStandard"
      capacity = 1
    }
    decision = {
      model    = "gpt-5-mini"
      version  = "2025-08-07"
      sku      = "DataZoneStandard"
      capacity = 1
    }
    embedding = {
      model    = "text-embedding-3-small"
      version  = "1"
      sku      = "DataZoneStandard"
      capacity = 1
    }
  }

  validation {
    condition = length(var.foundry_deployments) > 0 && alltrue([
      for name, deployment in var.foundry_deployments :
      can(regex("^[a-z0-9-]{1,64}$", name))
      && trimspace(deployment.model) != ""
      && trimspace(deployment.version) != ""
      && contains(["DataZoneStandard", "GlobalStandard", "Standard"], deployment.sku)
      && deployment.capacity >= 1
      && deployment.capacity <= 1000
    ])
    error_message = "foundry_deployments must be non-empty and use valid names, models, versions, pay-per-token SKUs, and capacities from 1 to 1000."
  }
}
