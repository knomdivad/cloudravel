-- Chief backfill: existing workspace memberships keep cloud monitoring only unless re-granted.
-- Idempotent: only rows not yet marked by this migration are reset.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo.user_tenant_access', N'entitlement_backfill_v011') IS NULL
BEGIN
    ALTER TABLE dbo.user_tenant_access
        ADD entitlement_backfill_v011 BIT NOT NULL
            CONSTRAINT DF_uta_entitlement_backfill_v011 DEFAULT 0;
END
GO

UPDATE dbo.user_tenant_access
SET entitlement_cloud_monitor = 1,
    entitlement_bot_monitor = 0,
    entitlement_backfill_v011 = 1
WHERE entitlement_backfill_v011 = 0;
GO
