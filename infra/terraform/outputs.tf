# ============================================================================
# Outputs — Azure Inventory Monitor
# ============================================================================

output "resource_group_name" {
  description = "Name of the deployed resource group"
  value       = azurerm_resource_group.main.name
}

output "function_app_name" {
  description = "Name of the Function App"
  value       = azurerm_linux_function_app.main.name
}

output "function_app_url" {
  description = "Default hostname of the Function App"
  value       = "https://${azurerm_linux_function_app.main.default_hostname}"
}

output "function_app_principal_id" {
  description = "Managed identity principal ID of the Function App"
  value       = azurerm_linux_function_app.main.identity[0].principal_id
}

output "static_web_app_name" {
  description = "Name of the Static Web App"
  value       = azurerm_static_web_app.main.name
}

output "static_web_app_url" {
  description = "Default hostname of the Static Web App"
  value       = "https://${azurerm_static_web_app.main.default_host_name}"
}

output "sql_server_fqdn" {
  description = "Fully qualified domain name of the SQL Server"
  value       = azurerm_mssql_server.main.fully_qualified_domain_name
}

output "key_vault_uri" {
  description = "URI of the Key Vault"
  value       = azurerm_key_vault.main.vault_uri
}

output "storage_account_name" {
  description = "Name of the Storage Account"
  value       = azurerm_storage_account.main.name
}

output "application_insights_connection_string" {
  description = "Connection string for Application Insights"
  value       = azurerm_application_insights.main.connection_string
  sensitive   = true
}

output "ai_foundry_endpoint" {
  description = "Inference endpoint of the Phi-4 serverless deployment"
  value       = local.ai_inference_uri
}

output "entra_app_client_id" {
  description = "Application (client) ID of the API app registration"
  value       = azuread_application.api.client_id
}
