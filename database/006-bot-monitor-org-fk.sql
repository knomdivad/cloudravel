-- BotMonitor tenants must reference an existing organization (portal workspace id).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_bot_monitor_tenants_organization')
BEGIN
    ALTER TABLE bot_monitor_tenants
        ADD CONSTRAINT FK_bot_monitor_tenants_organization
        FOREIGN KEY (tenant_id) REFERENCES organizations(org_id);
END
GO
