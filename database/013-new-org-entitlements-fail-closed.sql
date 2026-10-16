-- New organizations fail closed: no paid features until system_admin grants the org.
-- Existing organization rows are not modified (012 backfill remains in effect).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_org_entitlement_bot_monitor')
    ALTER TABLE dbo.organizations DROP CONSTRAINT DF_org_entitlement_bot_monitor;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_org_entitlement_cloud_monitor')
    ALTER TABLE dbo.organizations DROP CONSTRAINT DF_org_entitlement_cloud_monitor;
GO

ALTER TABLE dbo.organizations
    ADD CONSTRAINT DF_org_entitlement_bot_monitor DEFAULT 0 FOR entitlement_bot_monitor;
GO

ALTER TABLE dbo.organizations
    ADD CONSTRAINT DF_org_entitlement_cloud_monitor DEFAULT 0 FOR entitlement_cloud_monitor;
GO
