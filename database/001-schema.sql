-- ============================================================================
-- Azure Inventory Monitor — Database Schema
-- Engine: Azure SQL Database (or PostgreSQL with minor syntax changes)
-- Isolation: Row-Level Security on tenant_id
-- ============================================================================

-- ============================================================================
-- 1. TENANT MANAGEMENT
-- ============================================================================

CREATE TABLE tenants (
    tenant_id           UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY,
    display_name        NVARCHAR(256)       NOT NULL,
    azure_tenant_id     NVARCHAR(36)        NOT NULL,  -- Customer's Entra ID tenant
    onboarding_method   NVARCHAR(20)        NOT NULL CHECK (onboarding_method IN ('lighthouse', 'app_registration')),
    status              NVARCHAR(20)        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'degraded', 'suspended', 'offboarded')),
    snapshot_frequency_minutes  INT         NOT NULL DEFAULT 360,  -- 6 hours
    change_poll_frequency_minutes INT       NOT NULL DEFAULT 15,
    key_vault_secret_name NVARCHAR(256)     NULL,      -- For app_registration method
    lighthouse_delegation_id NVARCHAR(256)  NULL,      -- For lighthouse method
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by          NVARCHAR(128)       NOT NULL,
    INDEX IX_tenants_azure_tid (azure_tenant_id)
);

CREATE TABLE tenant_subscriptions (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL REFERENCES tenants(tenant_id),
    subscription_id     NVARCHAR(36)        NOT NULL,
    subscription_name   NVARCHAR(256)       NOT NULL,
    status              NVARCHAR(20)        NOT NULL DEFAULT 'active',
    discovered_at       DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    last_seen_at        DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    INDEX IX_ts_tenant (tenant_id),
    UNIQUE (tenant_id, subscription_id)
);

-- ============================================================================
-- 2. USER & RBAC
-- ============================================================================

CREATE TABLE users (
    user_id             UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY,  -- Entra object ID
    display_name        NVARCHAR(256)       NOT NULL,
    email               NVARCHAR(256)       NOT NULL,
    global_role         NVARCHAR(20)        NOT NULL DEFAULT 'operator' CHECK (global_role IN ('admin', 'operator', 'auditor')),
    is_active           BIT                 NOT NULL DEFAULT 1,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    last_login_at       DATETIME2           NULL,
    INDEX IX_users_email (email)
);

CREATE TABLE user_tenant_access (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id             UNIQUEIDENTIFIER    NOT NULL REFERENCES users(user_id),
    tenant_id           UNIQUEIDENTIFIER    NOT NULL REFERENCES tenants(tenant_id),
    role                NVARCHAR(20)        NOT NULL CHECK (role IN ('admin', 'operator', 'auditor', 'customer_admin', 'customer_viewer')),
    granted_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    granted_by          UNIQUEIDENTIFIER    NOT NULL,
    UNIQUE (user_id, tenant_id)
);

-- ============================================================================
-- 3. INVENTORY SNAPSHOTS
-- ============================================================================

CREATE TABLE inventory_snapshots (
    snapshot_id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL REFERENCES tenants(tenant_id),
    started_at          DATETIME2           NOT NULL,
    completed_at        DATETIME2           NULL,
    status              NVARCHAR(20)        NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'failed', 'partial')),
    resource_count      INT                 NULL,
    blob_path           NVARCHAR(1024)      NULL,      -- Path to raw ARI output in Blob Storage
    error_message       NVARCHAR(4000)      NULL,
    triggered_by        NVARCHAR(50)        NOT NULL DEFAULT 'schedule',  -- schedule, manual, onboarding
    INDEX IX_snapshots_tenant (tenant_id, started_at DESC)
);

CREATE TABLE inventory_resources (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    snapshot_id         BIGINT              NOT NULL REFERENCES inventory_snapshots(snapshot_id),
    resource_id         NVARCHAR(1024)      NOT NULL,  -- Full ARM resource ID
    subscription_id     NVARCHAR(36)        NOT NULL,
    resource_group      NVARCHAR(256)       NOT NULL,
    resource_type       NVARCHAR(256)       NOT NULL,  -- e.g., Microsoft.Compute/virtualMachines
    resource_name       NVARCHAR(256)       NOT NULL,
    location            NVARCHAR(64)        NOT NULL,
    sku_name            NVARCHAR(128)       NULL,
    sku_tier            NVARCHAR(64)        NULL,
    sku_capacity        INT                 NULL,
    tags                NVARCHAR(MAX)       NULL,      -- JSON object of tags
    identity_type       NVARCHAR(50)        NULL,      -- None, SystemAssigned, UserAssigned, SystemAndUser
    identity_principal_ids NVARCHAR(MAX)    NULL,      -- JSON array
    properties_json     NVARCHAR(MAX)       NULL,      -- Full ARM properties blob
    networking_json     NVARCHAR(MAX)       NULL,      -- Extracted networking config (IPs, NSGs, vnets)
    security_config_json NVARCHAR(MAX)      NULL,      -- Extracted security-relevant config
    INDEX IX_ir_tenant_snapshot (tenant_id, snapshot_id),
    INDEX IX_ir_resource_id (resource_id),
    INDEX IX_ir_resource_type (tenant_id, resource_type),
    INDEX IX_ir_subscription (tenant_id, subscription_id)
);

-- Pointer to the latest completed snapshot per tenant
CREATE TABLE latest_snapshots (
    tenant_id           UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY REFERENCES tenants(tenant_id),
    snapshot_id         BIGINT              NOT NULL REFERENCES inventory_snapshots(snapshot_id),
    updated_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ============================================================================
-- 4. CHANGE INTELLIGENCE
-- ============================================================================

CREATE TABLE resource_changes (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    change_id           NVARCHAR(256)       NOT NULL,  -- From Resource Graph changeId
    resource_id         NVARCHAR(1024)      NOT NULL,
    resource_type       NVARCHAR(256)       NOT NULL,
    change_type         NVARCHAR(20)        NOT NULL CHECK (change_type IN ('Create', 'Update', 'Delete')),
    detected_at         DATETIME2           NOT NULL,  -- When Resource Graph recorded it
    ingested_at         DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    changed_properties  NVARCHAR(MAX)       NULL,      -- JSON: [{ path, before, after }]
    actor_type          NVARCHAR(20)        NULL,      -- user, servicePrincipal, managedIdentity
    actor_id            NVARCHAR(256)       NULL,      -- Object ID of actor
    actor_name          NVARCHAR(256)       NULL,      -- Display name or app name
    client_type         NVARCHAR(64)        NULL,      -- AzurePortal, AzureCLI, Terraform, ARM, etc.
    classification      NVARCHAR(20)        NOT NULL DEFAULT 'operational' CHECK (classification IN ('security', 'governance', 'cost', 'operational')),
    severity            NVARCHAR(10)        NULL CHECK (severity IN ('critical', 'high', 'medium', 'low', 'info')),
    INDEX IX_rc_tenant_time (tenant_id, detected_at DESC),
    INDEX IX_rc_resource (tenant_id, resource_id),
    INDEX IX_rc_classification (tenant_id, classification),
    UNIQUE (tenant_id, change_id)
);

CREATE TABLE activity_log_events (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    event_id            NVARCHAR(256)       NOT NULL,
    resource_id         NVARCHAR(1024)      NULL,
    operation_name      NVARCHAR(512)       NOT NULL,  -- e.g., Microsoft.Compute/virtualMachines/write
    category            NVARCHAR(64)        NOT NULL,  -- Administrative, Security, Alert, etc.
    result_type         NVARCHAR(20)        NOT NULL,  -- Success, Failure, Start
    caller              NVARCHAR(256)       NULL,
    caller_ip           NVARCHAR(45)        NULL,
    event_timestamp     DATETIME2           NOT NULL,
    ingested_at         DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    properties_json     NVARCHAR(MAX)       NULL,
    INDEX IX_ale_tenant_time (tenant_id, event_timestamp DESC),
    INDEX IX_ale_resource (tenant_id, resource_id),
    UNIQUE (tenant_id, event_id)
);

-- ============================================================================
-- 5. RECOMMENDATIONS & RISK
-- ============================================================================

CREATE TABLE advisor_recommendations (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    recommendation_id   NVARCHAR(512)       NOT NULL,  -- ARM resource ID of the recommendation
    resource_id         NVARCHAR(1024)      NULL,      -- Impacted resource
    category            NVARCHAR(30)        NOT NULL,  -- HighAvailability, Security, Performance, Cost, OperationalExcellence
    impact              NVARCHAR(10)        NOT NULL,  -- High, Medium, Low
    title               NVARCHAR(512)       NOT NULL,
    description         NVARCHAR(4000)      NULL,
    remediation_action  NVARCHAR(4000)      NULL,
    estimated_savings   DECIMAL(18,2)       NULL,      -- Annual USD for cost recommendations
    currency            NVARCHAR(3)         NULL DEFAULT 'USD',
    first_seen_at       DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    last_seen_at        DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    resolved_at         DATETIME2           NULL,
    lifecycle_status    NVARCHAR(20)        NOT NULL DEFAULT 'active' CHECK (lifecycle_status IN ('active', 'acknowledged', 'snoozed', 'resolved', 'dismissed')),
    acknowledged_by     UNIQUEIDENTIFIER    NULL,
    INDEX IX_ar_tenant (tenant_id, lifecycle_status),
    INDEX IX_ar_resource (tenant_id, resource_id),
    INDEX IX_ar_category (tenant_id, category),
    UNIQUE (tenant_id, recommendation_id)
);

CREATE TABLE policy_compliance (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    policy_assignment_id NVARCHAR(1024)     NOT NULL,
    policy_definition_id NVARCHAR(1024)     NOT NULL,
    policy_name         NVARCHAR(512)       NOT NULL,
    resource_id         NVARCHAR(1024)      NOT NULL,
    compliance_state    NVARCHAR(20)        NOT NULL CHECK (compliance_state IN ('Compliant', 'NonCompliant', 'Exempt', 'Unknown')),
    category            NVARCHAR(64)        NULL,      -- Custom categorization
    first_seen_at       DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    last_evaluated_at   DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    resolved_at         DATETIME2           NULL,
    INDEX IX_pc_tenant (tenant_id, compliance_state),
    INDEX IX_pc_resource (tenant_id, resource_id),
    INDEX IX_pc_policy (tenant_id, policy_definition_id)
);

CREATE TABLE defender_findings (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    finding_id          NVARCHAR(512)       NOT NULL,
    resource_id         NVARCHAR(1024)      NULL,
    assessment_name     NVARCHAR(512)       NOT NULL,
    severity            NVARCHAR(10)        NOT NULL CHECK (severity IN ('Critical', 'High', 'Medium', 'Low', 'Informational')),
    status              NVARCHAR(20)        NOT NULL CHECK (status IN ('Unhealthy', 'Healthy', 'NotApplicable')),
    description         NVARCHAR(4000)      NULL,
    remediation_steps   NVARCHAR(4000)      NULL,
    categories          NVARCHAR(512)       NULL,      -- JSON array of category strings
    first_seen_at       DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    last_seen_at        DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    resolved_at         DATETIME2           NULL,
    INDEX IX_df_tenant (tenant_id, status),
    INDEX IX_df_resource (tenant_id, resource_id),
    INDEX IX_df_severity (tenant_id, severity)
);

-- ============================================================================
-- 6. AI AGENT AUDIT
-- ============================================================================

CREATE TABLE ai_query_log (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    user_id             UNIQUEIDENTIFIER    NOT NULL,
    query_text          NVARCHAR(4000)      NOT NULL,  -- User's original question
    tools_invoked       NVARCHAR(MAX)       NULL,      -- JSON: [{ tool, args, duration_ms }]
    response_text       NVARCHAR(MAX)       NULL,
    model_used          NVARCHAR(64)        NOT NULL,
    total_tokens        INT                 NULL,
    prompt_tokens       INT                 NULL,
    completion_tokens   INT                 NULL,
    duration_ms         INT                 NULL,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    INDEX IX_aql_tenant (tenant_id, created_at DESC),
    INDEX IX_aql_user (user_id, created_at DESC)
);

-- ============================================================================
-- 7. AUDIT TRAIL
-- ============================================================================

CREATE TABLE audit_events (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NULL,      -- NULL for global operations
    user_id             UNIQUEIDENTIFIER    NOT NULL,
    action              NVARCHAR(64)        NOT NULL,  -- tenant.onboard, user.grant, export.run, etc.
    entity_type         NVARCHAR(64)        NOT NULL,  -- tenant, user, snapshot, etc.
    entity_id           NVARCHAR(256)       NOT NULL,
    details_json        NVARCHAR(MAX)       NULL,
    client_ip           NVARCHAR(45)        NULL,
    user_agent          NVARCHAR(512)       NULL,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    INDEX IX_ae_tenant_time (tenant_id, created_at DESC),
    INDEX IX_ae_user (user_id, created_at DESC),
    INDEX IX_ae_action (action, created_at DESC)
);

-- ============================================================================
-- 8. ROW-LEVEL SECURITY
-- ============================================================================
GO

-- Security function: filters rows by tenant_id from session context
CREATE FUNCTION dbo.fn_tenant_security_predicate(@tenant_id UNIQUEIDENTIFIER)
RETURNS TABLE
WITH SCHEMABINDING
AS
    RETURN SELECT 1 AS result
    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS UNIQUEIDENTIFIER)
       OR CAST(SESSION_CONTEXT(N'bypass_rls') AS INT) = 1; -- Admin bypass for maintenance
GO

-- Apply RLS to all tenant-scoped tables
CREATE SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_snapshots,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_resources,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.resource_changes,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.activity_log_events,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.advisor_recommendations,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.policy_compliance,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.defender_findings,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.ai_query_log,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_resources,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.resource_changes,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.activity_log_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.advisor_recommendations,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.policy_compliance,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.defender_findings,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.ai_query_log
    WITH (STATE = ON);
GO

-- ============================================================================
-- 9. DATA RETENTION (Partitioning by month for efficient pruning)
-- ============================================================================

-- Note: Azure SQL supports table partitioning on Premium/Business Critical tiers.
-- For Standard tier, use indexed views + scheduled cleanup job instead.
GO

-- Monthly cleanup procedure (called by Azure Automation on a schedule)
CREATE PROCEDURE dbo.sp_cleanup_old_data
    @retention_months INT = 13
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @cutoff DATETIME2 = DATEADD(MONTH, -@retention_months, SYSUTCDATETIME());
    
    -- Delete old inventory resources (cascade from snapshots)
    DELETE ir FROM inventory_resources ir
    INNER JOIN inventory_snapshots s ON ir.snapshot_id = s.snapshot_id
    WHERE s.completed_at < @cutoff
      AND s.snapshot_id NOT IN (SELECT snapshot_id FROM latest_snapshots);
    
    -- Delete old snapshots (keep latest per tenant)
    DELETE FROM inventory_snapshots
    WHERE completed_at < @cutoff
      AND snapshot_id NOT IN (SELECT snapshot_id FROM latest_snapshots);
    
    -- Delete old change records
    DELETE FROM resource_changes WHERE detected_at < @cutoff;
    
    -- Delete old activity log events
    DELETE FROM activity_log_events WHERE event_timestamp < @cutoff;
    
    -- Do NOT delete resolved recommendations < cutoff (keep for trending)
    -- Instead, archive to a separate table or cold storage
    
    -- Delete old AI query logs
    DELETE FROM ai_query_log WHERE created_at < @cutoff;
END;
GO

-- ============================================================================
-- 10. USEFUL VIEWS
-- ============================================================================
GO

-- Current inventory: latest snapshot per tenant joined to resources
CREATE VIEW dbo.vw_current_inventory AS
SELECT 
    ir.tenant_id,
    ir.resource_id,
    ir.subscription_id,
    ir.resource_group,
    ir.resource_type,
    ir.resource_name,
    ir.location,
    ir.sku_name,
    ir.sku_tier,
    ir.tags,
    ir.identity_type,
    ir.properties_json,
    ir.networking_json,
    ir.security_config_json,
    ir.snapshot_id,
    s.completed_at AS snapshot_time
FROM inventory_resources ir
INNER JOIN latest_snapshots ls ON ir.tenant_id = ls.tenant_id AND ir.snapshot_id = ls.snapshot_id
INNER JOIN inventory_snapshots s ON ir.snapshot_id = s.snapshot_id;
GO

-- Open findings: active/unhealthy across all recommendation sources
CREATE VIEW dbo.vw_open_findings AS
SELECT 
    tenant_id,
    'advisor' AS source,
    recommendation_id AS finding_id,
    resource_id,
    category,
    impact AS severity,
    title,
    description,
    first_seen_at,
    last_seen_at
FROM advisor_recommendations
WHERE lifecycle_status IN ('active', 'acknowledged')

UNION ALL

SELECT 
    tenant_id,
    'policy' AS source,
    CAST(id AS NVARCHAR(256)) AS finding_id,
    resource_id,
    category,
    'Medium' AS severity,  -- Policy doesn't have severity; default Medium
    policy_name AS title,
    NULL AS description,
    first_seen_at,
    last_evaluated_at AS last_seen_at
FROM policy_compliance
WHERE compliance_state = 'NonCompliant'

UNION ALL

SELECT 
    tenant_id,
    'defender' AS source,
    finding_id,
    resource_id,
    COALESCE(categories, 'Security') AS category,
    severity,
    assessment_name AS title,
    description,
    first_seen_at,
    last_seen_at
FROM defender_findings
WHERE status = 'Unhealthy';
GO

-- Tenant health summary
CREATE VIEW dbo.vw_tenant_health AS
SELECT 
    t.tenant_id,
    t.display_name,
    t.status AS tenant_status,
    ls.updated_at AS last_snapshot_at,
    (SELECT COUNT(*) FROM vw_current_inventory ci WHERE ci.tenant_id = t.tenant_id) AS resource_count,
    (SELECT COUNT(*) FROM resource_changes rc WHERE rc.tenant_id = t.tenant_id AND rc.detected_at > DATEADD(HOUR, -24, SYSUTCDATETIME())) AS changes_24h,
    (SELECT COUNT(*) FROM advisor_recommendations ar WHERE ar.tenant_id = t.tenant_id AND ar.lifecycle_status = 'active') AS open_advisor_recs,
    (SELECT COUNT(*) FROM policy_compliance pc WHERE pc.tenant_id = t.tenant_id AND pc.compliance_state = 'NonCompliant') AS noncompliant_policies,
    (SELECT COUNT(*) FROM defender_findings df WHERE df.tenant_id = t.tenant_id AND df.status = 'Unhealthy') AS open_defender_findings
FROM tenants t
LEFT JOIN latest_snapshots ls ON t.tenant_id = ls.tenant_id;
GO
