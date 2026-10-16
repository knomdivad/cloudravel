-- Chief backfill: existing workspace memberships keep cloud monitoring only unless re-granted.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

UPDATE dbo.user_tenant_access
SET entitlement_cloud_monitor = 1,
    entitlement_bot_monitor = 0;
GO
