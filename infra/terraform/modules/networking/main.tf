# ============================================================================
# Networking Module — VNet, Subnets, Private Endpoints, DNS Zones
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

variable "tags" {
  type = map(string)
}

variable "sql_server_id" {
  type = string
}

variable "key_vault_id" {
  type = string
}

variable "storage_account_id" {
  type = string
}

variable "servicebus_id" {
  type = string
}

variable "is_production" {
  type    = bool
  default = false
}

# ============================================================================
# Virtual Network
# ============================================================================

resource "azurerm_virtual_network" "main" {
  name                = "vnet-${var.suffix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  address_space       = ["10.0.0.0/16"]
  tags                = var.tags
}

# ============================================================================
# Subnets
# ============================================================================

resource "azurerm_subnet" "function_integration" {
  name                 = "snet-function-integration"
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.0.1.0/24"]

  delegation {
    name = "functionapp"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

resource "azurerm_subnet" "private_endpoints" {
  name                 = "snet-private-endpoints"
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.0.2.0/24"]
}

# ============================================================================
# Private DNS Zones
# ============================================================================

locals {
  private_dns_zones = merge(
    {
      sql      = "privatelink.database.windows.net"
      keyvault = "privatelink.vaultcore.azure.net"
      blob     = "privatelink.blob.core.windows.net"
    },
    var.is_production ? {
      servicebus = "privatelink.servicebus.windows.net"
    } : {}
  )
}

resource "azurerm_private_dns_zone" "zones" {
  for_each            = local.private_dns_zones
  name                = each.value
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "links" {
  for_each              = local.private_dns_zones
  name                  = "link-${each.key}-${var.suffix}"
  resource_group_name   = var.resource_group_name
  private_dns_zone_name = azurerm_private_dns_zone.zones[each.key].name
  virtual_network_id    = azurerm_virtual_network.main.id
  registration_enabled  = false
  tags                  = var.tags
}

# ============================================================================
# Private Endpoints
# ============================================================================

locals {
  private_endpoints = merge(
    {
      sql = {
        name               = "pe-sql-${var.suffix}"
        target_resource_id = var.sql_server_id
        subresource_names  = ["sqlServer"]
        dns_zone_key       = "sql"
      }
      keyvault = {
        name               = "pe-kv-${var.suffix}"
        target_resource_id = var.key_vault_id
        subresource_names  = ["vault"]
        dns_zone_key       = "keyvault"
      }
      blob = {
        name               = "pe-blob-${var.suffix}"
        target_resource_id = var.storage_account_id
        subresource_names  = ["blob"]
        dns_zone_key       = "blob"
      }
    },
    var.is_production ? {
      servicebus = {
        name               = "pe-sb-${var.suffix}"
        target_resource_id = var.servicebus_id
        subresource_names  = ["namespace"]
        dns_zone_key       = "servicebus"
      }
    } : {}
  )
}

resource "azurerm_private_endpoint" "endpoints" {
  for_each            = local.private_endpoints
  name                = each.value.name
  location            = var.location
  resource_group_name = var.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = var.tags

  private_service_connection {
    name                           = "${each.value.name}-conn"
    private_connection_resource_id = each.value.target_resource_id
    subresource_names              = each.value.subresource_names
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name = "default"
    private_dns_zone_ids = [
      azurerm_private_dns_zone.zones[each.value.dns_zone_key].id
    ]
  }
}

# ============================================================================
# Outputs
# ============================================================================

output "vnet_id" {
  value = azurerm_virtual_network.main.id
}

output "function_integration_subnet_id" {
  value = azurerm_subnet.function_integration.id
}

output "private_endpoints_subnet_id" {
  value = azurerm_subnet.private_endpoints.id
}
