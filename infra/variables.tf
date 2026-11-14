variable "resource_group_name" {
  type        = string
  description = "Name of the owner-created production resource group."
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
  description = "Container image and tag to deploy."
  default     = "mcr.microsoft.com/k8se/quickstart:latest"
}

variable "sql_entra_admin_name" {
  type        = string
  description = "Display name of the Entra user or group that administers Azure SQL."
}

variable "sql_entra_admin_object_id" {
  type        = string
  description = "Object ID of the Entra user or group that administers Azure SQL."
}

variable "budget_alert_email" {
  type        = string
  description = "Email address for monthly budget alerts."
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
