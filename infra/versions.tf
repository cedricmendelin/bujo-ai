terraform {
  required_version = "~> 1.17.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.81.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10.0"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.12.0"
    }
    modtm = {
      source  = "Azure/modtm"
      version = "~> 0.3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7.0"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.13.0"
    }
  }
}
