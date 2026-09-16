-- Seed a small verify estate under a given org id (idempotent).
-- Usage: sqlcmd -v Org=<guid> (or invoked with the org id as $1 by verify-assessment.sh)
EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;

DECLARE @o UNIQUEIDENTIFIER = '$(Org)';
DECLARE @n DATETIME2 = SYSUTCDATETIME();

IF NOT EXISTS (SELECT 1 FROM inventory_snapshots WHERE tenant_id = @o)
BEGIN
    DECLARE @s BIGINT;

    INSERT INTO inventory_snapshots (tenant_id, started_at, completed_at, status, resource_count, triggered_by)
    VALUES (@o, DATEADD(DAY, -1, @n), DATEADD(DAY, -1, @n), 'completed', 3, 'manual');
    SET @s = SCOPE_IDENTITY();

    INSERT INTO latest_snapshots (tenant_id, snapshot_id) VALUES (@o, @s);

    INSERT INTO inventory_resources
        (tenant_id, snapshot_id, resource_id, subscription_id, resource_group, resource_type, resource_name, location)
    VALUES
        (@o, @s, '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', '11111111-1111-1111-1111-111111111111', 'rg-1', 'Microsoft.Compute/virtualMachines', 'vm-app-01', 'eastus'),
        (@o, @s, '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Storage/storageAccounts/stverify001', '11111111-1111-1111-1111-111111111111', 'rg-1', 'Microsoft.Storage/storageAccounts', 'stverify001', 'eastus'),
        (@o, @s, 'arn:aws:ec2:us-east-1:123456789012:instance/i-0abc', '123456789012', 'ec2', 'aws-ec2-instance', 'i-0abc', 'us-east-1');

    INSERT INTO advisor_recommendations
        (tenant_id, recommendation_id, resource_id, category, impact, title, description, remediation_action, estimated_savings, first_seen_at, last_seen_at, lifecycle_status)
    VALUES
        (@o, 'rec-v-001', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', 'Cost', 'High', 'Right-size underutilized virtual machine', 'CPU below 5% for 7 days.', 'Resize to smaller SKU.', 1840.00, DATEADD(DAY, -5, @n), @n, 'active'),
        (@o, 'rec-v-002', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Storage/storageAccounts/stverify001', 'Cost', 'Medium', 'Delete unattached disk', 'Disk not attached to any VM.', 'Delete the disk.', 260.00, DATEADD(DAY, -4, @n), @n, 'active'),
        (@o, 'rec-v-003', '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01', 'Cost', 'Low', 'Buy reserved instance', 'Consistent usage 30 days.', 'Purchase 1-year RI.', NULL, DATEADD(DAY, -3, @n), @n, 'active');

    INSERT INTO anomalies
        (tenant_id, fingerprint, kind, severity, status, provider, title, description, resource_id, detected_at, last_seen_at)
    VALUES
        (@o, 'fp-verify-1', 'CostAnomaly', 'High', 'Open', 'Azure', 'Advisor savings total jumped 120% week-over-week', 'Cost recommendations spiked', '/subscriptions/11111111-1111-1111-1111-111111111111', @n, @n);
END;
