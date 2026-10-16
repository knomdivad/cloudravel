-- Paid feature sets at the organization (workspace) level.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo.organizations', N'entitlement_bot_monitor') IS NULL
BEGIN
    ALTER TABLE dbo.organizations
        ADD entitlement_bot_monitor BIT NOT NULL
            CONSTRAINT DF_org_entitlement_bot_monitor DEFAULT 1;
END
GO

IF COL_LENGTH(N'dbo.organizations', N'entitlement_cloud_monitor') IS NULL
BEGIN
    ALTER TABLE dbo.organizations
        ADD entitlement_cloud_monitor BIT NOT NULL
            CONSTRAINT DF_org_entitlement_cloud_monitor DEFAULT 1;
END
GO

UPDATE dbo.organizations
SET entitlement_bot_monitor = 1,
    entitlement_cloud_monitor = 1
WHERE entitlement_bot_monitor = 0 AND entitlement_cloud_monitor = 0;
GO
