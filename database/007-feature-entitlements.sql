-- Per-user feature entitlements within an organization (deny by default for new grants).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo.user_tenant_access', N'entitlement_bot_monitor') IS NULL
BEGIN
    ALTER TABLE user_tenant_access
        ADD entitlement_bot_monitor BIT NOT NULL
            CONSTRAINT DF_uta_entitlement_bot_monitor DEFAULT 0;
END
GO

IF COL_LENGTH(N'dbo.user_tenant_access', N'entitlement_cloud_monitor') IS NULL
BEGIN
    ALTER TABLE user_tenant_access
        ADD entitlement_cloud_monitor BIT NOT NULL
            CONSTRAINT DF_uta_entitlement_cloud_monitor DEFAULT 0;
END
GO

UPDATE user_tenant_access
SET entitlement_bot_monitor = 1,
    entitlement_cloud_monitor = 1
WHERE entitlement_bot_monitor = 0 AND entitlement_cloud_monitor = 0;
GO

IF COL_LENGTH(N'dbo.bot_monitor_bots', N'owner_user_id') IS NULL
BEGIN
    ALTER TABLE bot_monitor_bots
        ADD owner_user_id UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_bmb_tenant_owner' AND object_id = OBJECT_ID(N'dbo.bot_monitor_bots'))
    CREATE INDEX IX_bmb_tenant_owner ON bot_monitor_bots(tenant_id, owner_user_id);
GO
