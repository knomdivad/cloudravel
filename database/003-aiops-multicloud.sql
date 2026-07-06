-- ============================================================================
-- Azure Inventory Monitor — AIOps + Multi-Cloud Extension (migration 003)
-- Adds: cloud accounts (AWS/GCP), anomalies + metric baselines, incidents +
--       timeline, remediation playbook catalog + gated remediation actions.
-- Run after 001-schema.sql and 002-fix-rls-bypass.sql.
--
-- Enum storage convention for new tables: PascalCase strings, matching the C#
-- enum names (Dapper parses them case-insensitively on read).
-- ============================================================================

-- ============================================================================
-- 1. TENANT POLICY COLUMNS
-- ============================================================================

ALTER TABLE tenants ADD
    auto_remediation_mode NVARCHAR(20) NOT NULL
        CONSTRAINT DF_tenants_auto_remediation DEFAULT 'gated'
        CHECK (auto_remediation_mode IN ('disabled', 'gated', 'auto')),
    aiops_monitoring_enabled BIT NOT NULL
        CONSTRAINT DF_tenants_aiops_monitoring DEFAULT 1;
GO

-- ============================================================================
-- 2. MULTI-CLOUD: provider column + linked accounts
-- ============================================================================

ALTER TABLE inventory_resources ADD
    provider NVARCHAR(10) NOT NULL
        CONSTRAINT DF_ir_provider DEFAULT 'azure'
        CHECK (provider IN ('azure', 'aws', 'gcp'));
GO

CREATE TABLE cloud_accounts (
    account_id          UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL REFERENCES tenants(tenant_id),
    provider            NVARCHAR(10)        NOT NULL CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    external_id         NVARCHAR(256)       NOT NULL,  -- AWS account ID / GCP project ID
    display_name        NVARCHAR(256)       NOT NULL,
    status              NVARCHAR(20)        NOT NULL DEFAULT 'Connected'
                          CHECK (status IN ('Connected', 'Degraded', 'Disconnected')),
    credential_secret_name NVARCHAR(256)    NULL,      -- Key Vault secret; SQL never holds credentials
    regions_json        NVARCHAR(MAX)       NULL,      -- JSON array of regions/zones to scan
    last_inventory_at   DATETIME2           NULL,
    last_error          NVARCHAR(2000)      NULL,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by          NVARCHAR(256)       NOT NULL,
    INDEX IX_ca_tenant (tenant_id),
    UNIQUE (tenant_id, provider, external_id)
);
GO

-- ============================================================================
-- 3. ANOMALIES + METRIC BASELINES
-- ============================================================================

CREATE TABLE anomalies (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    fingerprint         NVARCHAR(64)        NOT NULL,  -- dedup key: hash of (kind, scope)
    kind                NVARCHAR(40)        NOT NULL CHECK (kind IN (
                          'ChangeVelocitySpike', 'SecurityPostureRegression', 'CostAnomaly',
                          'ConfigurationDrift', 'UnusualActorActivity', 'StaleTelemetry', 'ResourceSprawl')),
    severity            NVARCHAR(10)        NOT NULL CHECK (severity IN ('Critical', 'High', 'Medium', 'Low', 'Info')),
    status              NVARCHAR(20)        NOT NULL DEFAULT 'Open'
                          CHECK (status IN ('Open', 'Acknowledged', 'Resolved', 'Suppressed', 'FalsePositive')),
    provider            NVARCHAR(10)        NOT NULL DEFAULT 'Azure' CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    title               NVARCHAR(512)       NOT NULL,
    description         NVARCHAR(4000)      NULL,
    resource_id         NVARCHAR(1024)      NULL,
    metric_name         NVARCHAR(128)       NULL,
    observed_value      FLOAT               NULL,
    baseline_mean       FLOAT               NULL,
    baseline_std_dev    FLOAT               NULL,
    score               FLOAT               NULL,      -- z-score / detector-specific significance
    detected_at         DATETIME2           NOT NULL,
    last_seen_at        DATETIME2           NOT NULL,
    resolved_at         DATETIME2           NULL,
    details_json        NVARCHAR(MAX)       NULL,      -- detector evidence (changes, findings, actors)
    incident_id         BIGINT              NULL,
    INDEX IX_an_tenant_status (tenant_id, status, last_seen_at DESC),
    INDEX IX_an_fingerprint (tenant_id, fingerprint),
    INDEX IX_an_incident (incident_id)
);
GO

CREATE TABLE metric_baselines (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    metric_key          NVARCHAR(128)       NOT NULL,  -- e.g. changes.total.24h, inventory.resource_count
    window_hours        INT                 NOT NULL DEFAULT 720,
    mean                FLOAT               NOT NULL,
    std_dev             FLOAT               NOT NULL DEFAULT 0,
    sample_count        INT                 NOT NULL DEFAULT 0,
    updated_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    UNIQUE (tenant_id, metric_key)
);
GO

-- ============================================================================
-- 4. INCIDENTS + TIMELINE
-- ============================================================================

CREATE TABLE incidents (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    title               NVARCHAR(512)       NOT NULL,
    severity            NVARCHAR(10)        NOT NULL CHECK (severity IN ('Critical', 'High', 'Medium', 'Low', 'Info')),
    status              NVARCHAR(20)        NOT NULL DEFAULT 'Open'
                          CHECK (status IN ('Open', 'Acknowledged', 'Mitigated', 'Resolved', 'Closed')),
    source              NVARCHAR(20)        NOT NULL DEFAULT 'anomaly' CHECK (source IN ('anomaly', 'manual', 'ai')),
    summary_markdown    NVARCHAR(MAX)       NULL,
    assigned_to         NVARCHAR(256)       NULL,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    acknowledged_at     DATETIME2           NULL,
    mitigated_at        DATETIME2           NULL,
    resolved_at         DATETIME2           NULL,
    closed_at           DATETIME2           NULL,
    sla_due_at          DATETIME2           NULL,      -- severity-based SLA clock
    INDEX IX_inc_tenant_status (tenant_id, status, created_at DESC),
    INDEX IX_inc_sla (tenant_id, sla_due_at)
);
GO

CREATE TABLE incident_events (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    incident_id         BIGINT              NOT NULL REFERENCES incidents(id),
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    occurred_at         DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    event_type          NVARCHAR(30)        NOT NULL CHECK (event_type IN (
                          'created', 'status_change', 'note', 'anomaly_linked', 'remediation_linked', 'ai_summary')),
    message             NVARCHAR(2000)      NOT NULL,
    actor_name          NVARCHAR(256)       NULL,
    details_json        NVARCHAR(MAX)       NULL,
    INDEX IX_ie_incident (incident_id, occurred_at)
);
GO

-- ============================================================================
-- 5. REMEDIATION: playbook catalog (global) + gated actions (tenant-scoped)
-- ============================================================================

CREATE TABLE remediation_playbooks (
    playbook_key        NVARCHAR(64)        NOT NULL PRIMARY KEY,
    display_name        NVARCHAR(256)       NOT NULL,
    description         NVARCHAR(2000)      NOT NULL,
    provider            NVARCHAR(10)        NOT NULL CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    category            NVARCHAR(20)        NOT NULL CHECK (category IN ('security', 'cost', 'operational', 'governance')),
    action_type         NVARCHAR(64)        NOT NULL,  -- executor key on the provider adapter
    risk_level          NVARCHAR(10)        NOT NULL DEFAULT 'Medium' CHECK (risk_level IN ('Low', 'Medium', 'High')),
    always_requires_approval BIT            NOT NULL DEFAULT 0,
    enabled             BIT                 NOT NULL DEFAULT 1,
    parameters_schema_json NVARCHAR(MAX)    NULL
);
GO

CREATE TABLE remediation_actions (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NOT NULL,
    playbook_key        NVARCHAR(64)        NOT NULL REFERENCES remediation_playbooks(playbook_key),
    provider            NVARCHAR(10)        NOT NULL CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    resource_id         NVARCHAR(1024)      NULL,
    title               NVARCHAR(512)       NOT NULL,
    reason              NVARCHAR(4000)      NOT NULL,  -- evidence shown to the approver
    parameters_json     NVARCHAR(MAX)       NULL,
    status              NVARCHAR(20)        NOT NULL DEFAULT 'Proposed' CHECK (status IN (
                          'Proposed', 'PendingApproval', 'Approved', 'Rejected',
                          'Executing', 'Succeeded', 'Failed', 'Cancelled', 'Expired')),
    risk_level          NVARCHAR(10)        NOT NULL DEFAULT 'Medium' CHECK (risk_level IN ('Low', 'Medium', 'High')),
    requested_by        NVARCHAR(256)       NOT NULL,  -- system:anomaly | ai:query | user:{name}
    anomaly_id          BIGINT              NULL,
    incident_id         BIGINT              NULL,
    approval_mode       NVARCHAR(10)        NOT NULL DEFAULT 'gated' CHECK (approval_mode IN ('auto', 'gated')),
    approved_by         NVARCHAR(256)       NULL,
    approved_at         DATETIME2           NULL,
    rejected_reason     NVARCHAR(1000)      NULL,
    executed_at         DATETIME2           NULL,
    completed_at        DATETIME2           NULL,
    result_json         NVARCHAR(MAX)       NULL,
    error_message       NVARCHAR(4000)      NULL,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    expires_at          DATETIME2           NULL,      -- stale pending approvals never fire
    INDEX IX_ra_tenant_status (tenant_id, status, created_at DESC),
    INDEX IX_ra_queue (status, approved_at),           -- execution queue scan
    INDEX IX_ra_incident (incident_id)
);
GO

-- ============================================================================
-- 6. ROW-LEVEL SECURITY for new tenant-scoped tables
--    (remediation_playbooks is a global catalog — deliberately not under RLS)
-- ============================================================================

ALTER SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_accounts,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.anomalies,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.metric_baselines,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incidents,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incident_events,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.remediation_actions,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_accounts,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.anomalies,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.metric_baselines,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incidents,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incident_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.remediation_actions;
GO

-- ============================================================================
-- 7. SEED: allow-listed remediation playbook catalog
--    Every action the platform can take exists here — nothing else executes.
-- ============================================================================

INSERT INTO remediation_playbooks
    (playbook_key, display_name, description, provider, category, action_type, risk_level, always_requires_approval, parameters_schema_json)
VALUES
    ('azure-storage-disable-public-blob', 'Disable public blob access',
     'Sets allowBlobPublicAccess=false on a storage account, reverting a common security drift. Reversible; may break workloads that rely on anonymous access.',
     'Azure', 'security', 'azure.storage.disable_public_blob', 'Low', 0, NULL),

    ('azure-storage-require-https', 'Require HTTPS-only transport',
     'Sets supportsHttpsTrafficOnly=true on a storage account. Reversible; breaks plain-HTTP clients (rare and undesirable).',
     'Azure', 'security', 'azure.storage.require_https', 'Low', 0, NULL),

    ('azure-vm-deallocate', 'Deallocate virtual machine',
     'Stops and deallocates a VM so compute charges stop. Workload downtime until restarted — approve only when the VM is confirmed idle/abandoned.',
     'Azure', 'cost', 'azure.vm.deallocate', 'Medium', 0, NULL),

    ('azure-vm-start', 'Start virtual machine',
     'Starts a deallocated VM (recovery playbook for accidental stops).',
     'Azure', 'operational', 'azure.vm.start', 'Low', 0, NULL),

    ('azure-apply-tags', 'Apply governance tags',
     'Merges required tags onto a resource via the ARM Tags API. Non-destructive.',
     'Azure', 'governance', 'azure.resource.apply_tags', 'Low', 0,
     '{"type":"object","properties":{"tags":{"type":"object","description":"Tag name/value pairs to merge"}},"required":["tags"]}'),

    ('azure-nsg-remove-rule', 'Remove NSG security rule',
     'Deletes a named inbound/outbound rule from a network security group (e.g. an any-any rule opened by mistake). May cut off legitimate traffic — always gated.',
     'Azure', 'security', 'azure.nsg.remove_rule', 'High', 1,
     '{"type":"object","properties":{"ruleName":{"type":"string","description":"Name of the security rule to delete"}},"required":["ruleName"]}'),

    ('aws-ec2-stop-instance', 'Stop EC2 instance',
     'Stops an EC2 instance so compute charges stop. Workload downtime until restarted.',
     'Aws', 'cost', 'aws.ec2.stop_instance', 'Medium', 0,
     '{"type":"object","properties":{"instanceId":{"type":"string"},"region":{"type":"string"}},"required":["instanceId"]}'),

    ('aws-s3-block-public-access', 'Block S3 public access',
     'Enables all four S3 Public Access Block settings on a bucket. Reversible; breaks intentional public hosting.',
     'Aws', 'security', 'aws.s3.block_public_access', 'Low', 0,
     '{"type":"object","properties":{"bucket":{"type":"string"},"region":{"type":"string"}},"required":["bucket"]}'),

    ('gcp-compute-stop-instance', 'Stop Compute Engine instance',
     'Stops a GCE instance so compute charges stop. Workload downtime until restarted.',
     'Gcp', 'cost', 'gcp.compute.stop_instance', 'Medium', 0,
     '{"type":"object","properties":{"zone":{"type":"string"},"instance":{"type":"string"},"project":{"type":"string"}},"required":["zone","instance"]}'),

    ('gcp-storage-enforce-pap', 'Enforce public access prevention',
     'Sets publicAccessPrevention=enforced on a Cloud Storage bucket. Reversible; breaks intentional public hosting.',
     'Gcp', 'security', 'gcp.storage.enforce_pap', 'Low', 0,
     '{"type":"object","properties":{"bucket":{"type":"string"}},"required":["bucket"]}');
GO

-- ============================================================================
-- 8. OPERATIONS VIEWS
-- ============================================================================

-- Live operational posture per tenant (feeds MSP-level fleet dashboards)
CREATE VIEW dbo.vw_tenant_ops_summary AS
SELECT
    t.tenant_id,
    t.display_name,
    t.auto_remediation_mode,
    t.aiops_monitoring_enabled,
    (SELECT COUNT(*) FROM anomalies a WHERE a.tenant_id = t.tenant_id AND a.status IN ('Open', 'Acknowledged')) AS open_anomalies,
    (SELECT COUNT(*) FROM anomalies a WHERE a.tenant_id = t.tenant_id AND a.status = 'Open' AND a.severity = 'Critical') AS critical_anomalies,
    (SELECT COUNT(*) FROM incidents i WHERE i.tenant_id = t.tenant_id AND i.status IN ('Open', 'Acknowledged', 'Mitigated')) AS open_incidents,
    (SELECT COUNT(*) FROM incidents i WHERE i.tenant_id = t.tenant_id AND i.status IN ('Open', 'Acknowledged', 'Mitigated')
        AND i.sla_due_at IS NOT NULL AND i.sla_due_at < SYSUTCDATETIME()) AS sla_breached_incidents,
    (SELECT COUNT(*) FROM remediation_actions r WHERE r.tenant_id = t.tenant_id AND r.status = 'PendingApproval') AS pending_approvals,
    (SELECT COUNT(*) FROM remediation_actions r WHERE r.tenant_id = t.tenant_id AND r.status = 'Succeeded'
        AND r.completed_at > DATEADD(DAY, -7, SYSUTCDATETIME())) AS remediations_7d
FROM tenants t;
GO
