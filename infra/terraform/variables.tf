# ============================================================================
# CloudRavel — Input Variables
# ============================================================================

variable "subscription_id" {
  description = "Azure subscription ID to deploy into"
  type        = string
}

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
  default     = "dev"

  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "Environment must be one of: dev, staging, prod."
  }
}

variable "location" {
  description = "Azure region for all resources"
  type        = string
  default     = "eastus2"
}

variable "base_name" {
  description = "Base name prefix for resources"
  type        = string
  default     = "cloudravel"
}

variable "sql_admin_username" {
  description = "SQL Server administrator login"
  type        = string
  default     = "cloudraveladmin"
}

variable "sql_admin_password" {
  description = "SQL Server administrator password"
  type        = string
  sensitive   = true
}

variable "ai_model_id" {
  description = "Azure AI Foundry model registry ID for the serverless deployment"
  type        = string
  default     = "azureml://registries/azureml/models/Phi-4"
}

variable "openai_model_name" {
  description = "Azure OpenAI model for the AIOps assistant (latest OpenAI model GA on Azure OpenAI)"
  type        = string
  default     = "gpt-5.5"
}

variable "openai_model_version" {
  description = "Azure OpenAI model version. Empty string selects the model's current default version."
  type        = string
  default     = ""
}

variable "openai_deployment_sku" {
  description = "Deployment SKU for the Azure OpenAI model (GlobalStandard recommended for GPT-5.x)"
  type        = string
  default     = "GlobalStandard"
}

variable "openai_capacity" {
  description = "Deployment capacity (throughput units, in thousands of TPM)"
  type        = number
  default     = 50
}

variable "ai_location" {
  description = "Azure region for AI Foundry resources"
  type        = string
  default     = "eastus2"
}

variable "alert_email" {
  description = "Email for alert notifications (optional, alerts are prod-only)"
  type        = string
  default     = ""
}

variable "tags" {
  description = "Tags to apply to all resources"
  type        = map(string)
  default = {
    "Application"  = "",
    "BusinessUnit" = "",
    "Compliance"   = "",
    "CostCenter"   = "",
    "ServiceLevel" = "",
    "environment"  = "dev",
    "managed_by"   = "terraform",
    "project"      = "cloudravel",
    "team"         = "cbts-ps"
  }
}
