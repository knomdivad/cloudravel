-- BotMonitor tenants must reference an existing organization (portal workspace id).
-- Idempotent: safe to execute multiple times (orphan cleanup then FK if missing).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'tempdb..#BotMonitorOrphanTenants') IS NOT NULL
    DROP TABLE #BotMonitorOrphanTenants;

CREATE TABLE #BotMonitorOrphanTenants (
    tenant_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
);

INSERT INTO #BotMonitorOrphanTenants (tenant_id)
SELECT t.tenant_id
FROM bot_monitor_tenants AS t
WHERE t.tenant_id NOT IN (SELECT org_id FROM organizations);
GO

DELETE n
FROM bot_monitor_nonces AS n
INNER JOIN bot_monitor_bots AS b ON b.bot_id = n.bot_id
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = b.tenant_id;
GO

DELETE r
FROM bot_monitor_heartbeat_rate AS r
INNER JOIN bot_monitor_bots AS b ON b.bot_id = r.bot_id
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = b.tenant_id;
GO

DELETE h
FROM bot_monitor_heartbeats AS h
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = h.tenant_id;
GO

DELETE s
FROM bot_monitor_bot_state AS s
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = s.tenant_id;
GO

DELETE m
FROM bot_monitor_magic_links AS m
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = m.tenant_id;
GO

DELETE b
FROM bot_monitor_bots AS b
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = b.tenant_id;
GO

DELETE t
FROM bot_monitor_tenants AS t
INNER JOIN #BotMonitorOrphanTenants AS o ON o.tenant_id = t.tenant_id;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_bot_monitor_tenants_organization')
BEGIN
    ALTER TABLE bot_monitor_tenants
        ADD CONSTRAINT FK_bot_monitor_tenants_organization
        FOREIGN KEY (tenant_id) REFERENCES organizations(org_id);
END
GO
