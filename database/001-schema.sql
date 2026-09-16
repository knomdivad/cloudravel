-- ============================================================================
-- CloudRavel — Database Schema
-- Engine: Azure SQL Database (or PostgreSQL with minor syntax changes)
-- Isolation: Row-Level Security on tenant_id
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

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
    secret_name         NVARCHAR(256)       NULL,      -- Secret name in the configured secret store (Key Vault or OpenBao); for app_registration method
    lighthouse_delegation_id NVARCHAR(256)  NULL,      -- For lighthouse method
    auto_remediation_mode NVARCHAR(20)      NOT NULL
        CONSTRAINT DF_tenants_auto_remediation DEFAULT 'gated'
        CHECK (auto_remediation_mode IN ('disabled', 'gated', 'auto')),
    aiops_monitoring_enabled BIT            NOT NULL
        CONSTRAINT DF_tenants_aiops_monitoring DEFAULT 1,
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
    user_id             UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY,  -- Entra object ID, or a generated GUID for local accounts
    display_name        NVARCHAR(256)       NOT NULL,
    email               NVARCHAR(256)       NOT NULL,
    global_role         NVARCHAR(20)        NOT NULL
        CONSTRAINT DF_users_global_role DEFAULT 'member'
        CONSTRAINT CK_users_global_role CHECK (global_role IN ('system_admin', 'member')),
    is_active           BIT                 NOT NULL DEFAULT 1,
    auth_provider       NVARCHAR(20)        NOT NULL
        CONSTRAINT DF_users_auth_provider DEFAULT 'entra'
        CHECK (auth_provider IN ('entra', 'local')),
    username            NVARCHAR(128)       NULL,      -- Local accounts only; mirrors email
    password_hash       NVARCHAR(256)       NULL,      -- pbkdf2$sha256$<iterations>$<saltBase64>$<hashBase64>
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    last_login_at       DATETIME2           NULL,
    INDEX IX_users_email (email)
);

-- Only local accounts have a username, and it must be unique among them.
CREATE UNIQUE INDEX UX_users_username ON users(username) WHERE username IS NOT NULL;

-- Email is the unique login identity (case-normalized to lower on write by the app).
CREATE UNIQUE INDEX UX_users_email ON users(email);

CREATE TABLE user_tenant_access (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id             UNIQUEIDENTIFIER    NOT NULL REFERENCES users(user_id),
    tenant_id           UNIQUEIDENTIFIER    NOT NULL REFERENCES tenants(tenant_id),
    role                NVARCHAR(20)        NOT NULL
        CONSTRAINT CK_uta_role CHECK (role IN ('org_admin', 'cloud_admin', 'read_only')),
    granted_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    granted_by          UNIQUEIDENTIFIER    NOT NULL,
    UNIQUE (user_id, tenant_id)
);

-- ============================================================================
-- 3. ORGANIZATIONS (the in-app workspace above clouds)
--
-- An Organization owns its clouds as peers:
--   * an Azure tenant  → a cloud_orgs row (provider 'Azure') + azure_org_subscriptions
--   * AWS Organizations → cloud_orgs (provider 'Aws')  + member accounts
--   * GCP Organizations → cloud_orgs (provider 'Gcp')  + projects
--
-- org_id IS the workspace / RLS boundary value — the same GUID stored in every
-- tenant_id column. Not RLS-protected: like `tenants`, it is the workspace
-- registry that DEFINES the boundary rather than being filtered by it.
-- ============================================================================

CREATE TABLE organizations (
    org_id      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    name        NVARCHAR(256)    NOT NULL,
    environment NVARCHAR(20)     NOT NULL DEFAULT 'Development'
                  CHECK (environment IN ('Development', 'Production')),
    status      NVARCHAR(20)     NOT NULL DEFAULT 'active'
                  CHECK (status IN ('active', 'suspended')),
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by  NVARCHAR(256)    NOT NULL DEFAULT 'system'
);

-- ============================================================================
-- 4. CLOUD ORGANIZATIONS (provider-agnostic peer grouping)
--
-- Top-level, provider-agnostic organization connection. Peers across
-- providers: an Azure tenant, an AWS Organization, or a GCP Organization.
-- tenant_id is the workspace / RLS boundary (the enterprise running the
-- platform), NOT an Azure-specific dependency.
-- ============================================================================

CREATE TABLE cloud_orgs (
    org_id                   UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    tenant_id                UNIQUEIDENTIFIER NOT NULL,   -- workspace / RLS boundary (the enterprise)
    provider                 NVARCHAR(10)     NOT NULL CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    name                     NVARCHAR(256)    NOT NULL,
    external_id              NVARCHAR(256)    NULL,        -- Azure tenant GUID / AWS Org ID / GCP Org ID
    status                   NVARCHAR(20)     NOT NULL DEFAULT 'Active'
                               CHECK (status IN ('Active', 'Degraded', 'Disconnected')),
    -- Azure-only connection fields, nullable/no-op for Aws/Gcp rows.
    onboarding_method        NVARCHAR(20)     NULL
                               CHECK (onboarding_method IN ('lighthouse', 'app_registration')),
    credential_secret_name   NVARCHAR(256)    NULL,
    lighthouse_delegation_id NVARCHAR(256)    NULL,
    subscription_scope       NVARCHAR(10)     NOT NULL
        CONSTRAINT DF_co_subscription_scope DEFAULT 'all'
        CHECK (subscription_scope IN ('all', 'specific')),
    created_at               DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by                NVARCHAR(256)    NOT NULL DEFAULT 'system',
    INDEX IX_co_tenant (tenant_id, provider)
);

-- Pinned subscriptions for an Azure connection whose subscription_scope='specific'.
-- (Only meaningful for provider='Azure' cloud_orgs rows; AWS/GCP use cloud_accounts
-- instead, since their members need independent credentials/health, unlike an Azure
-- subscription, which is just a scope filter under its connection's one credential.)
CREATE TABLE azure_org_subscriptions (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    org_id              UNIQUEIDENTIFIER NOT NULL REFERENCES cloud_orgs(org_id),
    tenant_id           UNIQUEIDENTIFIER NOT NULL,   -- workspace / RLS boundary
    subscription_id     NVARCHAR(36)     NOT NULL,
    subscription_name   NVARCHAR(256)    NOT NULL,
    created_at          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    UNIQUE (org_id, subscription_id),
    INDEX IX_aos_org (org_id)
);

-- Multi-cloud account (AWS account / GCP project), attached to a cloud_orgs
-- connection. tenant_id is the workspace/RLS boundary only — not a hard FK to
-- `tenants`, so AWS/GCP accounts never depend on onboarding an Azure tenant.
CREATE TABLE cloud_accounts (
    account_id          UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY,
    tenant_id            UNIQUEIDENTIFIER    NOT NULL,
    org_id               UNIQUEIDENTIFIER    NULL REFERENCES cloud_orgs(org_id),
    provider            NVARCHAR(10)        NOT NULL CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    external_id         NVARCHAR(256)       NOT NULL,  -- AWS account ID / GCP project ID
    display_name        NVARCHAR(256)       NOT NULL,
    status              NVARCHAR(20)        NOT NULL DEFAULT 'Connected'
                          CHECK (status IN ('Connected', 'Degraded', 'Disconnected')),
    credential_secret_name NVARCHAR(256)    NULL,      -- Secret name in the configured secret store; SQL never holds credentials
    regions_json        NVARCHAR(MAX)       NULL,      -- JSON array of regions/zones to scan
    last_inventory_at   DATETIME2           NULL,
    last_error          NVARCHAR(2000)      NULL,
    created_at          DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by          NVARCHAR(256)       NOT NULL,
    INDEX IX_ca_tenant (tenant_id),
    UNIQUE (tenant_id, provider, external_id)
);

-- ============================================================================
-- 5. INVENTORY SNAPSHOTS
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
    provider             NVARCHAR(10)        NOT NULL
        CONSTRAINT DF_ir_provider DEFAULT 'azure'
        CHECK (provider IN ('azure', 'aws', 'gcp')),
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
-- 6. CHANGE INTELLIGENCE
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
    caller               NVARCHAR(256)       NULL,
    caller_ip           NVARCHAR(45)        NULL,
    event_timestamp     DATETIME2           NOT NULL,
    ingested_at         DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    properties_json     NVARCHAR(MAX)       NULL,
    INDEX IX_ale_tenant_time (tenant_id, event_timestamp DESC),
    INDEX IX_ale_resource (tenant_id, resource_id),
    UNIQUE (tenant_id, event_id)
);

-- ============================================================================
-- 7. RECOMMENDATIONS & RISK
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
-- 8. AIOPS: ANOMALIES + METRIC BASELINES
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

-- ============================================================================
-- 9. INCIDENTS + TIMELINE
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

-- ============================================================================
-- 10. REMEDIATION: playbook catalog (global) + gated actions (tenant-scoped)
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

-- ============================================================================
-- 11. AI AGENT AUDIT
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
-- 12. AUDIT TRAIL
-- ============================================================================

CREATE TABLE audit_events (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id           UNIQUEIDENTIFIER    NULL,      -- NULL for global operations
    user_id             UNIQUEIDENTIFIER    NOT NULL,
    action               NVARCHAR(64)        NOT NULL,  -- tenant.onboard, user.grant, export.run, etc.
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
-- 13. JOB QUEUE (cloud-agnostic)
--
-- Backs IJobQueue's DatabaseJobQueue implementation — a SQL-table-based
-- outbox/queue used when no Azure Service Bus connection is configured (local
-- dev, or self-hosting on a non-Azure cloud). AzureServiceBusJobQueue remains
-- the default when ServiceBusConnection IS configured; this table is simply
-- unused in that case. Not RLS-scoped — admin/worker-only, no tenant_id column.
-- ============================================================================

CREATE TABLE job_queue (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    queue_name      NVARCHAR(100)   NOT NULL,
    payload_json    NVARCHAR(MAX)   NOT NULL,
    created_at      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    available_at    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),  -- supports delayed retry
    dequeued_at     DATETIME2       NULL,
    processed_at    DATETIME2       NULL,
    attempts        INT             NOT NULL DEFAULT 0,
    error           NVARCHAR(2000)  NULL,
    INDEX IX_job_queue_pending (queue_name, available_at) INCLUDE (processed_at)
);

-- ============================================================================
-- 14. SYSTEM & SSO SETTINGS
-- ============================================================================

-- Global key/value config (NOT RLS-scoped; system-admin only at the app
-- layer). Secret values (e.g. the OpenAI API key) are NOT stored here; only a
-- secret NAME pointing at the secret store, mirroring cloud creds.
CREATE TABLE system_settings (
    setting_key   NVARCHAR(128) NOT NULL PRIMARY KEY,
    setting_value NVARCHAR(MAX) NULL,
    updated_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_by    NVARCHAR(256) NOT NULL DEFAULT 'system'
);

-- Per-organization SSO config. Any client secret goes to the secret store;
-- only its name is kept here. Per-org token federation (multi-issuer
-- validation, user->org mapping) is a documented follow-up.
CREATE TABLE org_sso_settings (
    org_id             UNIQUEIDENTIFIER NOT NULL PRIMARY KEY REFERENCES organizations(org_id),
    provider           NVARCHAR(20)     NOT NULL DEFAULT 'none'
                         CHECK (provider IN ('none', 'entra', 'oidc')),
    idp_tenant_id      NVARCHAR(256)    NULL,
    idp_client_id      NVARCHAR(256)    NULL,
    domain             NVARCHAR(256)    NULL,
    client_secret_name NVARCHAR(256)    NULL,
    enabled            BIT              NOT NULL DEFAULT 0,
    updated_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_by         NVARCHAR(256)    NOT NULL DEFAULT 'system'
);
GO

-- ============================================================================
-- 15. ROW-LEVEL SECURITY
-- ============================================================================

-- Security function: filters rows by tenant_id from session context
CREATE FUNCTION dbo.fn_tenant_security_predicate(@tenant_id UNIQUEIDENTIFIER)
RETURNS TABLE
WITH SCHEMABINDING
AS
    RETURN SELECT 1 AS result
    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS UNIQUEIDENTIFIER)
       OR CAST(SESSION_CONTEXT(N'bypass_rls') AS INT) = 1; -- Admin bypass for maintenance
GO

-- Apply RLS to every tenant-scoped table.
-- Not RLS-scoped: tenants, tenant_subscriptions, users, user_tenant_access
-- (session/workspace-registry tables, not filtered data), organizations,
-- remediation_playbooks (global catalog), job_queue, system_settings,
-- org_sso_settings.
CREATE SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_snapshots,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_resources,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.resource_changes,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.activity_log_events,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.advisor_recommendations,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.policy_compliance,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.defender_findings,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.ai_query_log,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.audit_events,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_orgs,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.azure_org_subscriptions,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_accounts,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.anomalies,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.metric_baselines,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incidents,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incident_events,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.remediation_actions,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_snapshots,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_resources,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.resource_changes,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.activity_log_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.advisor_recommendations,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.policy_compliance,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.defender_findings,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.ai_query_log,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.audit_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_orgs,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.azure_org_subscriptions,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_accounts,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.anomalies,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.metric_baselines,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incidents,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.incident_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.remediation_actions
    WITH (STATE = ON);
GO

-- ============================================================================
-- 16. DATA RETENTION (Partitioning by month for efficient pruning)
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
-- 17. USEFUL VIEWS
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

-- ============================================================================
-- 18. SEED DATA
-- ============================================================================

-- Allow-listed remediation playbook catalog. Every action the platform can
-- take exists here — nothing else executes.
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

-- Bootstrap local admin so a fresh deployment has a working login immediately.
--
--   email / username: admin@local
--   password:          ChangeMe123!   <-- DEV DEFAULT. Change this immediately
--                                         outside of local/dev use; this hash
--                                         is public (it's in source control).
IF NOT EXISTS (SELECT 1 FROM users WHERE username = 'admin@local' OR email = 'admin@local')
BEGIN
    INSERT INTO users (user_id, display_name, email, global_role, is_active, auth_provider, username, password_hash)
    VALUES (
        'a1000000-0000-0000-0000-000000000001',
        'Local Admin',
        'admin@local',
        'system_admin',
        1,
        'local',
        'admin@local',
        'pbkdf2$sha256$210000$2gNPz+6njzR/uNEO1g3o9A==$zaisf4nCNps9iP/VJ++Io6KzgyPXL2FEzg4Ux22FYpE='
    );
END
GO
