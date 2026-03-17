# ============================================================================
# Monitoring Module — Diagnostics, Alerts, Action Groups
# ============================================================================

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "suffix" {
  type = string
}

variable "is_production" {
  type = bool
}

variable "tags" {
  type = map(string)
}

variable "log_analytics_workspace_id" {
  type = string
}

variable "key_vault_id" {
  type = string
}

variable "servicebus_id" {
  type = string
}

variable "sql_database_id" {
  type = string
}

variable "function_app_id" {
  type = string
}

variable "alert_email" {
  type    = string
  default = ""
}

# ============================================================================
# Diagnostic Settings
# ============================================================================

# Key Vault diagnostics
resource "azurerm_monitor_diagnostic_setting" "key_vault" {
  name                       = "diag-kv-${var.suffix}"
  target_resource_id         = var.key_vault_id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category = "AuditEvent"
  }

  enabled_log {
    category = "AzurePolicyEvaluationDetails"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

# Service Bus diagnostics
resource "azurerm_monitor_diagnostic_setting" "servicebus" {
  name                       = "diag-sb-${var.suffix}"
  target_resource_id         = var.servicebus_id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category = "OperationalLogs"
  }

  enabled_log {
    category = "VNetAndIPFilteringLogs"
  }

  enabled_log {
    category = "RuntimeAuditLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

# SQL Database diagnostics
resource "azurerm_monitor_diagnostic_setting" "sql_database" {
  name                       = "diag-sql-${var.suffix}"
  target_resource_id         = var.sql_database_id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category = "SQLSecurityAuditEvents"
  }

  enabled_log {
    category = "QueryStoreRuntimeStatistics"
  }

  enabled_log {
    category = "Errors"
  }

  enabled_log {
    category = "Timeouts"
  }

  enabled_metric {
    category = "Basic"
  }
}

# ============================================================================
# Action Group (conditional)
# ============================================================================

resource "azurerm_monitor_action_group" "main" {
  count               = var.alert_email != "" ? 1 : 0
  name                = "ag-${var.suffix}"
  resource_group_name = var.resource_group_name
  short_name          = "aim-alerts"
  tags                = var.tags

  email_receiver {
    name                    = "AIM-Admin"
    email_address           = var.alert_email
    use_common_alert_schema = true
  }
}

# ============================================================================
# Metric Alerts (production only)
# ============================================================================

locals {
  action_group_id = var.alert_email != "" ? azurerm_monitor_action_group.main[0].id : null
  create_alerts   = var.is_production && var.alert_email != ""
}

# HTTP 5xx errors on Function App
resource "azurerm_monitor_metric_alert" "http_5xx" {
  count               = local.create_alerts ? 1 : 0
  name                = "alert-http5xx-${var.suffix}"
  resource_group_name = var.resource_group_name
  scopes              = [var.function_app_id]
  description         = "Alert when HTTP 5xx errors exceed threshold"
  severity            = 1
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Web/sites"
    metric_name      = "Http5xx"
    aggregation      = "Total"
    operator         = "GreaterThan"
    threshold        = 10
  }

  action {
    action_group_id = local.action_group_id
  }
}

# High response time on Function App
resource "azurerm_monitor_metric_alert" "http_latency" {
  count               = local.create_alerts ? 1 : 0
  name                = "alert-latency-${var.suffix}"
  resource_group_name = var.resource_group_name
  scopes              = [var.function_app_id]
  description         = "Alert when HTTP response time exceeds threshold"
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Web/sites"
    metric_name      = "HttpResponseTime"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 5
  }

  action {
    action_group_id = local.action_group_id
  }
}

# High DTU on SQL Database
resource "azurerm_monitor_metric_alert" "sql_dtu" {
  count               = local.create_alerts ? 1 : 0
  name                = "alert-dtu-${var.suffix}"
  resource_group_name = var.resource_group_name
  scopes              = [var.sql_database_id]
  description         = "Alert when SQL Database DTU consumption exceeds threshold"
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Sql/servers/databases"
    metric_name      = "dtu_consumption_percent"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 85
  }

  action {
    action_group_id = local.action_group_id
  }
}

# Dead-lettered messages on Service Bus
resource "azurerm_monitor_metric_alert" "sb_deadletter" {
  count               = local.create_alerts ? 1 : 0
  name                = "alert-deadletter-${var.suffix}"
  resource_group_name = var.resource_group_name
  scopes              = [var.servicebus_id]
  description         = "Alert when dead-lettered messages exceed threshold"
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.ServiceBus/namespaces"
    metric_name      = "DeadletteredMessages"
    aggregation      = "Total"
    operator         = "GreaterThan"
    threshold        = 5
  }

  action {
    action_group_id = local.action_group_id
  }
}
