terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }

  # Remote state — configure per environment.
  # Uncomment and set values for your Azure Storage backend.
  backend "azurerm" {
    resource_group_name  = "rg-cbts-automation-shared-services"
    storage_account_name = "cbts"
    container_name       = "terraform-state"
    key                  = "aim/infra.tfstate"
    subscription_id      = "c7c5469f-b7ec-4a91-ae0d-2607354009a9"
    use_oidc             = true
    use_azuread_auth     = true
  }
}

provider "azurerm" {
  subscription_id     = var.subscription_id
  storage_use_azuread = true
  features {
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
    cognitive_account {
      purge_soft_delete_on_destroy = false
    }
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
  # Authentication is handled via environment variables:
  #   az login                          — local dev
  #   ARM_USE_OIDC=true + ARM_CLIENT_ID — CI/CD
}

provider "azapi" {
  subscription_id = var.subscription_id
}

provider "azuread" {
  # Authentication is handled via the same ARM_* environment variables as azurerm
}
