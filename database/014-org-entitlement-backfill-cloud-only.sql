-- Chief backfill: existing organizations keep cloud monitoring only (bot off until system_admin licenses).
-- Idempotent via entitlement_backfill_v014; new orgs set the flag on insert so they stay fully unlicensed.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo.organizations', N'entitlement_backfill_v014') IS NULL
BEGIN
    ALTER TABLE dbo.organizations
        ADD entitlement_backfill_v014 BIT NOT NULL
            CONSTRAINT DF_org_entitlement_backfill_v014 DEFAULT 0;
END
GO

UPDATE dbo.organizations
SET entitlement_cloud_monitor = 1,
    entitlement_bot_monitor = 0,
    entitlement_backfill_v014 = 1
WHERE entitlement_backfill_v014 = 0;
GO
