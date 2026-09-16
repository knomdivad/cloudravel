-- ============================================================================
-- Seed a verify estate for the assessment-engine E2E run (idempotent).
-- Tenant: 7a5e0000-0000-0000-0000-00000000e501 ("E2E Verify Corp")
-- Grants admin@local org_admin; seeds $1,840 + $260 advisor items, an
-- unquantified RI rec, a cost anomaly, a security anomaly, 3 resources,
-- a completed snapshot, and a change inside the 7-day window.
-- Run: sqlcmd -v (no vars needed; ids are fixed).
-- ============================================================================
EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;

DECLARE @t UNIQUEIDENTIFIER = '7a5e0000-0000-0000-0000-00000000e501';
DECLARE @n DATETIME2 = SYSUTCDATETIME();

IF NOT EXISTS (SELECT 1 FROM tenants WHERE tenant_id = @t)
    INSERT INTO tenants (tenant_id, display_name, azure_tenant_id, onboarding_method, status, created_by)
    VALUES (@t, 'E2E Verify Corp', '11111111-1111-1111-1111-111111111111', 'lighthouse', 'active', 'e2e-seed');

-- The verifying principal (system admin) needs org access for tenant-scoped reads.
IF NOT EXISTS (SELECT 1 FROM user_tenant_access WHERE user_id = 'a1000000-0000-0000-0000-000000000001' AND tenant_id = @t)
    INSERT INTO user_tenant_access (user_id, tenant_id, role, granted_at, granted_by)
    VALUES ('a1000000-0000-0000-0000-000000000001', @t, 'org_admin', @n, 'a1000000-0000-0000-0000-000000000001');

IF NOT EXISTS (SELECT 1 FROM inventory_snapshots WHERE tenant_id = @t)
BEGIN
    DECLARE @s BIGINT;

    INSERT INTO inventory_snapshots (tenant_id, started_at, completed_at, status, resource_count, triggered_by)
    VALUES (@t, DATEADD(DAY, -1, @n), DATEADD(DAY, -1, @n), 'completed', 3, 'manual');
    SET @s = SCOPE_IDENTITY();

    INSERT INTO latest_snapshots (tenant_id, snapshot_id) VALUES (@t, @s);

    INSERT INTO inventory_resources
        (tenant_id, snapshot_id, resource_id, subscription_id, resource_group, resource_type, resource_name, location)
    VALUES
        (@t, @s, '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', '11111111-1111-1111-1111-111111111111', 'rg-1', 'Microsoft.Compute/virtualMachines', 'vm-app-01', 'eastus'),
        (@t, @s, '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Storage/storageAccounts/ste2e001', '11111111-1111-1111-1111-111111111111', 'rg-1', 'Microsoft.Storage/storageAccounts', 'ste2e001', 'eastus'),
        (@t, @s, 'arn:aws:ec2:us-east-1:123456789012:instance/i-0abc', '123456789012', 'ec2', 'aws-ec2-instance', 'i-0abc', 'us-east-1');

    INSERT INTO advisor_recommendations
        (tenant_id, recommendation_id, resource_id, category, impact, title, description, remediation_action, estimated_savings, first_seen_at, last_seen_at, lifecycle_status)
    VALUES
        (@t, 'rec-e2e-001', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', 'Cost', 'High', 'Right-size underutilized virtual machine', 'CPU below 5% for 7 days.', 'Resize to smaller SKU.', 1840.00, DATEADD(DAY, -5, @n), @n, 'active'),
        (@t, 'rec-e2e-002', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Storage/storageAccounts/ste2e001', 'Cost', 'Medium', 'Delete unattached disk', 'Disk not attached to any VM.', 'Delete the disk.', 260.00, DATEADD(DAY, -4, @n), @n, 'active'),
        (@t, 'rec-e2e-003', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', 'Cost', 'Low', 'Buy reserved instance', 'Consistent usage 30 days.', 'Purchase 1-year RI.', NULL, DATEADD(DAY, -3, @n), @n, 'active');

    INSERT INTO anomalies
        (tenant_id, fingerprint, kind, severity, status, provider, title, description, resource_id, detected_at, last_seen_at)
    VALUES
        (@t, 'fp-e2e-cost-1', 'CostAnomaly', 'High', 'Open', 'Azure', 'Advisor savings total jumped 120% week-over-week', 'Cost recommendations spiked', '/subscriptions/11111111-1111-1111-1111-111111111111', @n, @n),
        (@t, 'fp-e2e-sec-1', 'SecurityPostureRegression', 'High', 'Open', 'Azure', 'New critical Defender findings', 'Security posture regressed', '/subscriptions/11111111-1111-1111-1111-111111111111', @n, @n);

    INSERT INTO resource_changes
        (tenant_id, change_id, resource_id, resource_type, change_type, detected_at, actor_name, actor_type, classification)
    VALUES
        (@t, 'chg-e2e-001', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', 'Microsoft.Compute/virtualMachines', 'Update', DATEADD(HOUR, -6, @n), 'ops@contoso.com', 'user', 'operational');
END;
