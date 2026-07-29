# ============================================================================
# CloudRavel — Main Terraform Configuration
# ============================================================================

locals {
  suffix        = "${var.base_name}-${var.environment}"
  is_production = var.environment == "prod"

  default_tags = merge(var.tags, {
    project     = "cloudravel"
    environment = var.environment
    managed_by  = "terraform"
  })
}

# Deterministic unique suffix for globally-unique resource names
resource "random_id" "unique" {
  byte_length = 4
  keepers = {
    base = var.base_name
    env  = var.environment
  }
}

locals {
  unique_suffix = "${var.base_name}${random_id.unique.hex}"
}

data "azurerm_client_config" "current" {}

# ============================================================================
# Resource Group
# ============================================================================

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.suffix}"
  location = var.location
  tags     = local.default_tags
}

# ============================================================================
# Log Analytics Workspace
# ============================================================================

resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${local.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 90
  tags                = local.default_tags
}

# ============================================================================
# Application Insights
# ============================================================================

resource "azurerm_application_insights" "main" {
  name                = "appi-${local.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
  retention_in_days   = 90
  tags                = local.default_tags
}

# ============================================================================
# Key Vault
# ============================================================================

resource "azurerm_key_vault" "main" {
  name                       = "kv-${local.unique_suffix}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  soft_delete_retention_days = 90
  purge_protection_enabled   = true
  tags                       = local.default_tags

  network_acls {
    bypass         = "AzureServices"
    default_action = local.is_production ? "Deny" : "Allow"
  }
}

# ============================================================================
# Storage Account (ARI snapshots + Function runtime)
# ============================================================================

resource "azurerm_storage_account" "main" {
  name                            = "st${local.unique_suffix}"
  location                        = azurerm_resource_group.main.location
  resource_group_name             = azurerm_resource_group.main.name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  account_kind                    = "StorageV2"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = true
  tags                            = local.default_tags

  network_rules {
    bypass         = ["AzureServices"]
    default_action = local.is_production ? "Deny" : "Allow"
  }
}

resource "azurerm_storage_container" "snapshots" {
  name                  = "ari-snapshots"
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}

# ============================================================================
# Service Bus Namespace + Queue
# ============================================================================

resource "azurerm_servicebus_namespace" "main" {
  name                = "sb-${local.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = local.is_production ? "Premium" : "Standard"
  minimum_tls_version = "1.2"
  local_auth_enabled  = false
  tags                = local.default_tags
}

resource "azurerm_servicebus_queue" "snapshot_ingestion" {
  name                                 = "snapshot-ingestion"
  namespace_id                         = azurerm_servicebus_namespace.main.id
  lock_duration                        = "PT5M"
  max_size_in_megabytes                = 1024
  requires_duplicate_detection         = false
  dead_lettering_on_message_expiration = true
  max_delivery_count                   = 5
  default_message_ttl                  = "P1D"
}

# ============================================================================
# Azure SQL Server + Database
# ============================================================================

resource "azurerm_mssql_server" "main" {
  name                          = "sql-${local.unique_suffix}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  version                       = "12.0"
  administrator_login           = var.sql_admin_username
  administrator_login_password  = var.sql_admin_password
  minimum_tls_version           = "1.2"
  public_network_access_enabled = local.is_production ? false : true
  tags                          = local.default_tags

  azuread_administrator {
    login_username = "cloudravel-platform-admin"
    object_id      = azurerm_linux_function_app.main.identity[0].principal_id
    tenant_id      = data.azurerm_client_config.current.tenant_id
  }

  depends_on = [azurerm_linux_function_app.main]
}

# Allow Azure services through the SQL firewall (dev/staging only)
resource "azurerm_mssql_firewall_rule" "allow_azure" {
  count            = local.is_production ? 0 : 1
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_database" "main" {
  name           = "cloudraveldb"
  server_id      = azurerm_mssql_server.main.id
  sku_name       = local.is_production ? "S3" : "S1"
  collation      = "SQL_Latin1_General_CP1_CI_AS"
  max_size_gb    = local.is_production ? 250 : 2
  zone_redundant = local.is_production
  tags           = local.default_tags

  transparent_data_encryption_enabled = true
}


# ============================================================================
# Entra ID App Registration (API authentication)
# ============================================================================

resource "azuread_application" "api" {
  display_name = "CloudRavel API (${var.environment})"

  sign_in_audience = "AzureADMyOrg"
  identifier_uris  = ["api://cloudravel-api"]

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      admin_consent_description  = "Access the CloudRavel API as a signed-in user"
      admin_consent_display_name = "Access CloudRavel API"
      id                         = random_uuid.api_scope.result
      type                       = "User"
      user_consent_description   = "Access the CloudRavel API on your behalf"
      user_consent_display_name  = "Access CloudRavel API"
      value                      = "access_as_user"
      enabled                    = true
    }
  }

  single_page_application {
    redirect_uris = [
      "https://${azurerm_static_web_app.main.default_host_name}/",
      "https://${azurerm_static_web_app.main.default_host_name}/auth/callback",
      "http://localhost:3000/",
      "http://localhost:3000/auth/callback",
    ]
  }

  web {
    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  tags = ["cloudravel", var.environment]
}

resource "random_uuid" "api_scope" {}

resource "azuread_service_principal" "api" {
  client_id = azuread_application.api.client_id
}

# ============================================================================
# Function App (API + Workers)
# ============================================================================

resource "azurerm_service_plan" "main" {
  name                = "plan-${local.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  os_type             = "Linux"
  sku_name            = local.is_production ? "EP1" : "Y1"
  tags                = local.default_tags
}

resource "azurerm_linux_function_app" "main" {
  name                        = "func-${local.suffix}"
  location                    = azurerm_resource_group.main.location
  resource_group_name         = azurerm_resource_group.main.name
  service_plan_id             = azurerm_service_plan.main.id
  storage_account_name        = azurerm_storage_account.main.name
  storage_account_access_key  = azurerm_storage_account.main.primary_access_key
  https_only                  = true
  functions_extension_version = "~4"
  tags                        = local.default_tags

  # VNet integration — production only (requires EP1+)
  virtual_network_subnet_id = local.is_production ? module.networking[0].function_integration_subnet_id : null

  identity {
    type = "SystemAssigned"
  }

  site_config {
    ftps_state          = "Disabled"
    minimum_tls_version = "1.2"

    application_insights_connection_string = azurerm_application_insights.main.connection_string

    application_stack {
      dotnet_version              = "8.0"
      use_dotnet_isolated_runtime = true
    }

    dynamic "cors" {
      for_each = azurerm_static_web_app.main.default_host_name != "" ? [1] : []
      content {
        allowed_origins = [
          "https://${azurerm_static_web_app.main.default_host_name}"
        ]
      }
    }
  }

  app_settings = {
    # Secrets: Azure Key Vault via ISecretStore (SecretStore:Provider=KeyVault)
    "SecretStore__Provider"                         = "KeyVault"
    "KeyVault__VaultUri"                            = azurerm_key_vault.main.vault_uri
    "SqlConnectionString"                           = "Server=tcp:sql-${local.unique_suffix}.database.windows.net,1433;Database=cloudraveldb;Authentication=Active Directory Managed Identity;Encrypt=True;"
    "ServiceBusConnection__fullyQualifiedNamespace" = "${azurerm_servicebus_namespace.main.name}.servicebus.windows.net"
    "BlobStorageUrl"                                = azurerm_storage_account.main.primary_blob_endpoint
    # App reads OpenAI:* (OpenAI-compatible client). Map Azure OpenAI here.
    "OpenAI__BaseUrl"                               = "${azurerm_cognitive_account.openai.endpoint}openai/v1"
    "OpenAI__ApiKey"                                = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault.main.vault_uri}secrets/AzureOpenAiApiKey)"
    "OpenAI__Model"                                 = azurerm_cognitive_deployment.gpt.name
    # Kept for transitional tooling that still reads the legacy names.
    "AzureOpenAI__Endpoint"                         = azurerm_cognitive_account.openai.endpoint
    "AzureOpenAI__ApiKey"                           = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault.main.vault_uri}secrets/AzureOpenAiApiKey)"
    "AzureOpenAI__DeploymentName"                   = azurerm_cognitive_deployment.gpt.name
    "LocalAuth__JwtSigningKey"                      = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault.main.vault_uri}secrets/LocalAuthJwtSigningKey)"
    "Cors__AllowedOrigins"                          = "https://${azurerm_static_web_app.main.default_host_name}"
    "Platform__Environment"                         = var.environment == "prod" ? "Production" : "Development"
    "AzureAd__TenantId"                             = data.azurerm_client_config.current.tenant_id
    "AzureAd__ClientId"                             = azuread_application.api.client_id
  }

  # Ensure the runtime storage RBAC roles exist before the Function App starts.
  # Note: Terraform may need a second apply if the Function App starts before
  # role propagation completes (~2 min). This is an Azure-side eventual consistency issue.
}

# ============================================================================
# Azure AI Foundry — Phi-4 (Models-as-a-Service)
# ============================================================================
# Provisions an AI Foundry Hub + Project + Phi-4 serverless endpoint via a
# single ARM template deployment.  We avoid azapi_resource for ML workspaces
# because the azapi provider has a confirmed bug: it cannot read
# MachineLearningServices/workspaces back after creation, causing
# "Missing Resource Identity After Read" on every subsequent plan.
# ============================================================================

# Safety net — drop old azapi resources from state without destroying Azure objects
removed {
  from = azapi_resource.ai_hub
  lifecycle { destroy = false }
}

removed {
  from = azapi_resource.ai_project
  lifecycle { destroy = false }
}

resource "azurerm_resource_group_template_deployment" "ai_foundry" {
  name                = "deploy-ai-foundry-${local.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  deployment_mode     = "Incremental"
  tags                = local.default_tags

  template_content = jsonencode({
    "$schema"      = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"
    contentVersion = "1.0.0.0"
    parameters     = {}
    resources = [
      {
        type       = "Microsoft.MachineLearningServices/workspaces"
        apiVersion = "2024-10-01"
        name       = "aihub-${local.suffix}"
        location   = var.ai_location
        tags       = local.default_tags
        kind       = "Hub"
        sku        = { name = "Basic", tier = "Basic" }
        identity   = { type = "SystemAssigned" }
        properties = {
          friendlyName        = "CloudRavel AI Hub (${var.environment})"
          storageAccount      = azurerm_storage_account.main.id
          keyVault            = azurerm_key_vault.main.id
          applicationInsights = azurerm_application_insights.main.id
          publicNetworkAccess = local.is_production ? "Disabled" : "Enabled"
        }
      },
      {
        type       = "Microsoft.MachineLearningServices/workspaces"
        apiVersion = "2024-10-01"
        name       = "aiproj-${local.suffix}"
        location   = var.ai_location
        tags       = local.default_tags
        kind       = "Project"
        sku        = { name = "Basic", tier = "Basic" }
        identity   = { type = "SystemAssigned" }
        properties = {
          friendlyName  = "CloudRavel AI Project (${var.environment})"
          hubResourceId = "[resourceId('Microsoft.MachineLearningServices/workspaces', 'aihub-${local.suffix}')]"
        }
        dependsOn = [
          "[resourceId('Microsoft.MachineLearningServices/workspaces', 'aihub-${local.suffix}')]"
        ]
      },
      {
        type       = "Microsoft.MachineLearningServices/workspaces/serverlessEndpoints"
        apiVersion = "2024-10-01"
        name       = "aiproj-${local.suffix}/phi4-${local.suffix}"
        location   = var.ai_location
        tags       = local.default_tags
        sku        = { name = "Consumption" }
        properties = {
          modelSettings = { modelId = var.ai_model_id }
          authMode      = "Key"
        }
        dependsOn = [
          "[resourceId('Microsoft.MachineLearningServices/workspaces', 'aiproj-${local.suffix}')]"
        ]
      }
    ]
    outputs = {
      endpointId = {
        type  = "string"
        value = "[resourceId('Microsoft.MachineLearningServices/workspaces/serverlessEndpoints', 'aiproj-${local.suffix}', 'phi4-${local.suffix}')]"
      }
      inferenceUri = {
        type  = "string"
        value = "[reference(resourceId('Microsoft.MachineLearningServices/workspaces/serverlessEndpoints', 'aiproj-${local.suffix}', 'phi4-${local.suffix}'), '2024-10-01').inferenceEndpoint.uri]"
      }
    }
  })
}

# Drop old single-endpoint ARM deployment from state
removed {
  from = azurerm_resource_group_template_deployment.ai_endpoint
  lifecycle { destroy = false }
}

locals {
  ai_endpoint_outputs = jsondecode(azurerm_resource_group_template_deployment.ai_foundry.output_content)
  ai_endpoint_id      = local.ai_endpoint_outputs.endpointId.value
  ai_inference_uri    = local.ai_endpoint_outputs.inferenceUri.value
}

# Retrieve the API key from the deployed serverless endpoint
data "azapi_resource_action" "ai_keys" {
  type        = "Microsoft.MachineLearningServices/workspaces/serverlessEndpoints@2024-10-01"
  resource_id = local.ai_endpoint_id
  action      = "listKeys"
  response_export_values = {
    primaryKey = "primaryKey"
  }

  depends_on = [azurerm_resource_group_template_deployment.ai_foundry]
}

# Store the API key in Key Vault.
# Uses az keyvault secret set (PUT semantic — create-or-update) so it never
# fails with "already exists" after a KV recovery from soft-delete.
resource "terraform_data" "ai_foundry_api_key" {
  triggers_replace = [
    data.azapi_resource_action.ai_keys.output.primaryKey,
    azurerm_key_vault.main.name,
  ]

  provisioner "local-exec" {
    command     = "az keyvault secret set --vault-name \"${azurerm_key_vault.main.name}\" --name AiFoundryApiKey --value \"$SECRET_VALUE\" --content-type \"application/x-api-key\" --output none"
    interpreter = ["bash", "-c"]
    environment = {
      SECRET_VALUE = data.azapi_resource_action.ai_keys.output.primaryKey
    }
  }

  depends_on = [azurerm_role_assignment.deployer_kv]
}

# Safety net — if the old azurerm_key_vault_secret is still in state, drop it
# without deleting the Azure-side secret.
removed {
  from = azurerm_key_vault_secret.ai_foundry_api_key
  lifecycle {
    destroy = false
  }
}

# ============================================================================
# Azure OpenAI — GPT-5.5 (AIOps assistant + AI query endpoint)
# The latest OpenAI model generally available on Azure OpenAI. Powers
# /api/ai/query tool calling and remediation proposals.
# ============================================================================

resource "azurerm_cognitive_account" "openai" {
  name                          = "oai-${local.suffix}"
  location                      = var.ai_location
  resource_group_name           = azurerm_resource_group.main.name
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = "oai-${local.unique_suffix}"
  public_network_access_enabled = local.is_production ? false : true
  tags                          = local.default_tags

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_cognitive_deployment" "gpt" {
  name                 = var.openai_model_name
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format = "OpenAI"
    name   = var.openai_model_name
    # Empty version → service default (current version of the model)
    version = var.openai_model_version != "" ? var.openai_model_version : null
  }

  sku {
    name     = var.openai_deployment_sku
    capacity = var.openai_capacity
  }
}

# Store the Azure OpenAI key in Key Vault (same PUT-semantics pattern as above)
resource "terraform_data" "azure_openai_api_key" {
  triggers_replace = [
    azurerm_cognitive_account.openai.primary_access_key,
    azurerm_key_vault.main.name,
  ]

  provisioner "local-exec" {
    command     = "az keyvault secret set --vault-name \"${azurerm_key_vault.main.name}\" --name AzureOpenAiApiKey --value \"$SECRET_VALUE\" --content-type \"application/x-api-key\" --output none"
    interpreter = ["bash", "-c"]
    environment = {
      SECRET_VALUE = azurerm_cognitive_account.openai.primary_access_key
    }
  }

  depends_on = [azurerm_role_assignment.deployer_kv]
}

# Local-auth JWT signing key (required by the Functions host). Generated once and
# stored in Key Vault; subsequent applies keep the same secret name/ref.
resource "random_password" "local_auth_jwt" {
  length  = 48
  special = false
}

resource "terraform_data" "local_auth_jwt_signing_key" {
  triggers_replace = [
    random_password.local_auth_jwt.result,
    azurerm_key_vault.main.name,
  ]

  provisioner "local-exec" {
    command     = "az keyvault secret set --vault-name \"${azurerm_key_vault.main.name}\" --name LocalAuthJwtSigningKey --value \"$SECRET_VALUE\" --content-type \"text/plain\" --output none"
    interpreter = ["bash", "-c"]
    environment = {
      SECRET_VALUE = random_password.local_auth_jwt.result
    }
  }

  depends_on = [azurerm_role_assignment.deployer_kv]
}

# ============================================================================
# Azure Automation Account (ARI execution)
# ============================================================================

resource "azurerm_automation_account" "main" {
  name                          = "auto-${local.suffix}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  sku_name                      = "Basic"
  public_network_access_enabled = false
  local_authentication_enabled  = false
  tags                          = local.default_tags

  identity {
    type = "SystemAssigned"
  }
}

# ============================================================================
# Static Web App (Frontend)
# ============================================================================

resource "azurerm_static_web_app" "main" {
  name                = "swa-${local.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku_tier            = local.is_production ? "Standard" : "Free"
  sku_size            = local.is_production ? "Standard" : "Free"
  tags                = local.default_tags
}

# ============================================================================
# RBAC — Function App runtime storage
# The Function App needs these to boot when using managed identity
# instead of storage access keys (shared_access_key_enabled = false).
# ============================================================================

resource "azurerm_role_assignment" "func_storage_owner" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "func_storage_queue" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "func_storage_table" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# ============================================================================
# RBAC — Application-level access
# ============================================================================

# Deployer (Terraform SP) → Key Vault Secrets Officer (write secrets during deploy)
resource "azurerm_role_assignment" "deployer_kv" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
  principal_type       = "ServicePrincipal"
}

# Function App → Key Vault Secrets Officer (read + write tenant credentials)
resource "azurerm_role_assignment" "func_kv" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Function App → Storage Blob Data Contributor (application data)
resource "azurerm_role_assignment" "func_storage_snapshots" {
  scope                = "${azurerm_storage_account.main.id}/blobServices/default/containers/${azurerm_storage_container.snapshots.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Function App → Service Bus Data Sender
resource "azurerm_role_assignment" "func_sb_sender" {
  scope                = azurerm_servicebus_namespace.main.id
  role_definition_name = "Azure Service Bus Data Sender"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Function App → Service Bus Data Receiver
resource "azurerm_role_assignment" "func_sb_receiver" {
  scope                = azurerm_servicebus_namespace.main.id
  role_definition_name = "Azure Service Bus Data Receiver"
  principal_id         = azurerm_linux_function_app.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}



# Automation Account → Storage Blob Data Contributor
resource "azurerm_role_assignment" "auto_storage" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_automation_account.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Automation Account → Service Bus Data Sender
resource "azurerm_role_assignment" "auto_sb_sender" {
  scope                = azurerm_servicebus_namespace.main.id
  role_definition_name = "Azure Service Bus Data Sender"
  principal_id         = azurerm_automation_account.main.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# ============================================================================
# Modules
# ============================================================================

module "networking" {
  source = "./modules/networking"
  count  = local.is_production ? 1 : 0

  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  suffix              = local.suffix
  tags                = local.default_tags
  is_production       = local.is_production

  sql_server_id      = azurerm_mssql_server.main.id
  key_vault_id       = azurerm_key_vault.main.id
  storage_account_id = azurerm_storage_account.main.id
  servicebus_id      = azurerm_servicebus_namespace.main.id

}

module "monitoring" {
  source = "./modules/monitoring"

  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  suffix                     = local.suffix
  is_production              = local.is_production
  tags                       = local.default_tags
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  key_vault_id    = azurerm_key_vault.main.id
  servicebus_id   = azurerm_servicebus_namespace.main.id
  sql_database_id = azurerm_mssql_database.main.id
  function_app_id = azurerm_linux_function_app.main.id
  alert_email     = var.alert_email
}


