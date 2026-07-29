-- ============================================================================
-- Demo seed data — OPTIONAL, not part of apply-migrations.sh
--
-- The migrator intentionally does NOT run this file. Fresh deploys get only
-- the schema + bootstrap local admin (004/011). Use this script when you want
-- Contoso sample inventory/AIOps rows for UI demos.
--
-- Safe to re-run: skips entirely if "Contoso Corp" already exists.
-- After migrations (001–012), e.g.:
--   sqlcmd -S localhost -d cloudraveldb -U sa -P "$MSSQL_SA_PASSWORD" -I \
--     -i database/seed-demo-data.sql
-- ============================================================================

EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;

IF EXISTS (SELECT 1 FROM tenants WHERE display_name = 'Contoso Corp')
BEGIN
    PRINT 'Contoso Corp already exists — skipping seed.';
    RETURN;
END

DECLARE @TenantId UNIQUEIDENTIFIER = 'C0000000-0000-0000-0000-000000000001';
DECLARE @AzureTenantId NVARCHAR(36) = '9c39a1a6-1c1e-4c1a-9a1a-5b6c7d8e9f01';
DECLARE @AzureSubId NVARCHAR(36) = 'D0000000-0000-0000-0000-000000000001';
DECLARE @AdminUserId UNIQUEIDENTIFIER = 'a1000000-0000-0000-0000-000000000001';
DECLARE @Now DATETIME2 = SYSUTCDATETIME();

-- ============================================================================
-- Tenant + Azure subscription
-- ============================================================================
INSERT INTO tenants
    (tenant_id, display_name, azure_tenant_id, onboarding_method, status,
     created_at, updated_at, created_by)
VALUES
    (@TenantId, 'Contoso Corp', @AzureTenantId, 'lighthouse', 'active',
     DATEADD(DAY, -90, @Now), @Now, CONVERT(NVARCHAR(128), @AdminUserId));

-- Organization workspace: the in-app "Organization" that owns Contoso's clouds
-- (Azure tenant above + AWS/GCP orgs below). org_id = the workspace id, and this
-- instance is labelled Development so its seed clouds are never contacted.
IF NOT EXISTS (SELECT 1 FROM organizations WHERE org_id = @TenantId)
    INSERT INTO organizations (org_id, name, environment, created_by)
    VALUES (@TenantId, 'Contoso Corp', 'Development', CONVERT(NVARCHAR(256), @AdminUserId));

INSERT INTO tenant_subscriptions
    (tenant_id, subscription_id, subscription_name, status, discovered_at, last_seen_at)
VALUES
    (@TenantId, @AzureSubId, 'Contoso Production', 'active', DATEADD(DAY, -90, @Now), @Now);

-- ============================================================================
-- Inventory snapshot (completed) + resources across Azure, AWS, GCP
-- ============================================================================
INSERT INTO inventory_snapshots
    (tenant_id, started_at, completed_at, status, resource_count, triggered_by)
VALUES
    (@TenantId, DATEADD(HOUR, -6, @Now), DATEADD(HOUR, -6, DATEADD(MINUTE, 4, @Now)), 'completed', 20, 'onboarding');

DECLARE @SnapshotId BIGINT = SCOPE_IDENTITY();

INSERT INTO latest_snapshots (tenant_id, snapshot_id, updated_at)
VALUES (@TenantId, @SnapshotId, @Now);

-- --- Azure resources ---
INSERT INTO inventory_resources
    (tenant_id, snapshot_id, resource_id, subscription_id, resource_group, resource_type, resource_name,
     location, sku_name, sku_tier, tags, identity_type, properties_json, provider)
VALUES
    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Compute/virtualMachines/vm-contoso-web-01',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Compute/virtualMachines', 'vm-contoso-web-01', 'East US',
     'Standard_D2s_v5', 'Standard', '{"environment":"prod","owner":"platform-team"}', 'SystemAssigned',
     '{"vmSize":"Standard_D2s_v5","osType":"Linux","provisioningState":"Succeeded"}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Compute/virtualMachines/vm-contoso-app-01',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Compute/virtualMachines', 'vm-contoso-app-01', 'East US',
     'Standard_D4s_v5', 'Standard', '{"environment":"prod","owner":"platform-team"}', NULL,
     '{"vmSize":"Standard_D4s_v5","osType":"Linux","provisioningState":"Succeeded"}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Storage/storageAccounts/stcontosoprod001',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Storage/storageAccounts', 'stcontosoprod001', 'East US',
     'Standard_LRS', 'Standard', '{"environment":"prod"}', NULL,
     '{"accessTier":"Hot","allowBlobPublicAccess":true,"minimumTlsVersion":"TLS1_0"}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/networkSecurityGroups/nsg-contoso-web',
     @AzureSubId, 'rg-shared-network', 'Microsoft.Network/networkSecurityGroups', 'nsg-contoso-web', 'East US',
     NULL, NULL, NULL, NULL,
     '{"securityRules":[{"name":"AllowRDP","access":"Allow","destinationPortRange":"3389","sourceAddressPrefix":"*"}]}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/virtualNetworks/vnet-contoso-hub',
     @AzureSubId, 'rg-shared-network', 'Microsoft.Network/virtualNetworks', 'vnet-contoso-hub', 'East US',
     NULL, NULL, NULL, NULL, '{"addressSpace":"10.0.0.0/16"}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Sql/servers/sql-contoso-prod',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Sql/servers', 'sql-contoso-prod', 'East US',
     NULL, NULL, NULL, NULL, '{"publicNetworkAccess":"Enabled","version":"12.0"}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Sql/servers/sql-contoso-prod/databases/sqldb-contoso-orders',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Sql/servers/databases', 'sqldb-contoso-orders', 'East US',
     'S3', 'Standard', '{"environment":"prod"}', NULL, '{"maxSizeGb":250}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.KeyVault/vaults/kv-contoso-prod',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.KeyVault/vaults', 'kv-contoso-prod', 'East US',
     'standard', NULL, NULL, NULL, '{"purgeProtection":true,"softDelete":true}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Web/sites/app-contoso-api',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Web/sites', 'app-contoso-api', 'East US',
     'P1v3', 'PremiumV3', '{"environment":"prod","owner":"app-team"}', NULL,
     '{"httpsOnly":true,"runtime":".NET 8"}', 'azure'),

    (@TenantId, @SnapshotId,
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Network/publicIPAddresses/pip-contoso-web-01',
     @AzureSubId, 'rg-prod-eastus', 'Microsoft.Network/publicIPAddresses', 'pip-contoso-web-01', 'East US',
     'Standard', NULL, NULL, NULL, NULL, 'azure');

-- --- AWS resources (account 123456789012) ---
INSERT INTO inventory_resources
    (tenant_id, snapshot_id, resource_id, subscription_id, resource_group, resource_type, resource_name,
     location, tags, properties_json, provider)
VALUES
    (@TenantId, @SnapshotId, 'arn:aws:ec2:us-east-1:123456789012:instance/i-0abcd1234efgh5678',
     '123456789012', 'ec2', 'ec2/instance', 'i-0abcd1234efgh5678', 'us-east-1',
     '{"Name":"contoso-worker-01","environment":"prod"}', '{"instanceType":"t3.large","state":"running"}', 'aws'),

    (@TenantId, @SnapshotId, 'arn:aws:s3:::contoso-prod-assets',
     '123456789012', 's3', 's3/bucket', 'contoso-prod-assets', 'us-east-1',
     '{"environment":"prod"}', '{"publicAccessBlock":false,"versioning":"Enabled"}', 'aws'),

    (@TenantId, @SnapshotId, 'arn:aws:rds:us-east-1:123456789012:db:contoso-orders-db',
     '123456789012', 'rds', 'rds/db', 'contoso-orders-db', 'us-east-1',
     '{"environment":"prod"}', '{"engine":"postgres","instanceClass":"db.t3.medium"}', 'aws'),

    (@TenantId, @SnapshotId, 'arn:aws:lambda:us-east-1:123456789012:function:contoso-invoice-processor',
     '123456789012', 'lambda', 'lambda/function', 'contoso-invoice-processor', 'us-east-1',
     NULL, '{"runtime":"dotnet8","memorySize":512}', 'aws'),

    (@TenantId, @SnapshotId, 'arn:aws:iam::123456789012:role/contoso-lambda-execution-role',
     '123456789012', 'iam', 'iam/role', 'contoso-lambda-execution-role', 'global',
     NULL, NULL, 'aws');

-- --- GCP resources (project contoso-prod-472019) ---
INSERT INTO inventory_resources
    (tenant_id, snapshot_id, resource_id, subscription_id, resource_group, resource_type, resource_name,
     location, tags, properties_json, provider)
VALUES
    (@TenantId, @SnapshotId, 'projects/contoso-prod-472019/zones/us-central1-a/instances/gce-contoso-worker-01',
     'contoso-prod-472019', 'compute', 'compute/instance', 'gce-contoso-worker-01', 'us-central1-a',
     '{"env":"prod"}', '{"machineType":"e2-standard-2","status":"RUNNING"}', 'gcp'),

    (@TenantId, @SnapshotId, 'projects/contoso-prod-472019/buckets/contoso-prod-backups',
     'contoso-prod-472019', 'storage', 'storage/bucket', 'contoso-prod-backups', 'us-central1',
     NULL, '{"publicAccessPrevention":"inherited"}', 'gcp'),

    (@TenantId, @SnapshotId, 'projects/contoso-prod-472019/instances/contoso-analytics-db',
     'contoso-prod-472019', 'sql', 'sql/instance', 'contoso-analytics-db', 'us-central1',
     NULL, '{"databaseVersion":"POSTGRES_15","tier":"db-custom-2-8192"}', 'gcp'),

    (@TenantId, @SnapshotId, 'projects/contoso-prod-472019/locations/us-central1/clusters/contoso-gke-prod',
     'contoso-prod-472019', 'container', 'container/cluster', 'contoso-gke-prod', 'us-central1',
     '{"env":"prod"}', '{"nodeCount":3,"releaseChannel":"REGULAR"}', 'gcp'),

    (@TenantId, @SnapshotId, 'projects/contoso-prod-472019/serviceAccounts/contoso-app@contoso-prod-472019.iam.gserviceaccount.com',
     'contoso-prod-472019', 'iam', 'iam/serviceAccount', 'contoso-app@contoso-prod-472019.iam.gserviceaccount.com', 'global',
     NULL, NULL, 'gcp');

-- ============================================================================
-- Cloud orgs (AWS + GCP) — independent peers of Azure, each grouping accounts
-- ============================================================================
DECLARE @AwsOrgId UNIQUEIDENTIFIER = 'C0000000-0000-0000-0000-00000000B001';
DECLARE @GcpOrgId UNIQUEIDENTIFIER = 'C0000000-0000-0000-0000-00000000B002';

INSERT INTO cloud_orgs (org_id, tenant_id, provider, name, external_id, status, created_at, created_by)
VALUES
    (@AwsOrgId, @TenantId, 'Aws', 'Contoso AWS Organization', 'o-abc123def4', 'Active',
     DATEADD(DAY, -60, @Now), CONVERT(NVARCHAR(128), @AdminUserId)),
    (@GcpOrgId, @TenantId, 'Gcp', 'Contoso GCP Organization', '849021304719', 'Active',
     DATEADD(DAY, -45, @Now), CONVERT(NVARCHAR(128), @AdminUserId));

-- ============================================================================
-- Linked cloud accounts (AWS accounts + GCP projects) under their orgs.
-- Azure works via tenant onboarding above.
-- ============================================================================
INSERT INTO cloud_accounts
    (account_id, tenant_id, org_id, provider, external_id, display_name, status, regions_json, last_inventory_at, created_at, created_by)
VALUES
    ('C0000000-0000-0000-0000-0000000000A1', @TenantId, @AwsOrgId, 'Aws', '123456789012', 'Contoso Production (AWS)',
     'Connected', '["us-east-1","us-west-2"]', DATEADD(HOUR, -6, @Now), DATEADD(DAY, -60, @Now), CONVERT(NVARCHAR(128), @AdminUserId)),

    ('C0000000-0000-0000-0000-0000000000A3', @TenantId, @AwsOrgId, 'Aws', '210987654321', 'Contoso Sandbox (AWS)',
     'Connected', '["us-east-1"]', DATEADD(HOUR, -6, @Now), DATEADD(DAY, -20, @Now), CONVERT(NVARCHAR(128), @AdminUserId)),

    ('C0000000-0000-0000-0000-0000000000A2', @TenantId, @GcpOrgId, 'Gcp', 'contoso-prod-472019', 'Contoso GCP Project',
     'Connected', '["us-central1"]', DATEADD(HOUR, -6, @Now), DATEADD(DAY, -45, @Now), CONVERT(NVARCHAR(128), @AdminUserId));

-- ============================================================================
-- Resource changes (last 24-48h, mixed classification/severity)
-- ============================================================================
INSERT INTO resource_changes
    (tenant_id, change_id, resource_id, resource_type, change_type, detected_at,
     changed_properties, actor_type, actor_id, actor_name, client_type, classification, severity)
VALUES
    (@TenantId, 'chg-0001',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/networkSecurityGroups/nsg-contoso-web',
     'Microsoft.Network/networkSecurityGroups', 'Update', DATEADD(HOUR, -3, @Now),
     '[{"path":"securityRules[0].destinationPortRange","before":"443","after":"3389"}]',
     'user', NULL, 'sarah.chen@contoso.com', 'AzurePortal', 'security', 'critical'),

    (@TenantId, 'chg-0002',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Storage/storageAccounts/stcontosoprod001',
     'Microsoft.Storage/storageAccounts', 'Update', DATEADD(HOUR, -5, @Now),
     '[{"path":"properties.allowBlobPublicAccess","before":"false","after":"true"}]',
     'servicePrincipal', NULL, 'devops-pipeline-sp', 'AzureCLI', 'security', 'high'),

    (@TenantId, 'chg-0003',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Compute/virtualMachines/vm-contoso-app-01',
     'Microsoft.Compute/virtualMachines', 'Update', DATEADD(HOUR, -8, @Now),
     '[{"path":"properties.hardwareProfile.vmSize","before":"Standard_D2s_v5","after":"Standard_D4s_v5"}]',
     'user', NULL, 'james.park@contoso.com', 'AzurePortal', 'cost', 'low'),

    (@TenantId, 'chg-0004',
     'arn:aws:s3:::contoso-prod-assets', 's3/bucket', 'Update', DATEADD(HOUR, -12, @Now),
     '[{"path":"publicAccessBlock","before":"true","after":"false"}]',
     'user', NULL, 'terraform-automation', 'AzureCLI', 'security', 'high'),

    (@TenantId, 'chg-0005',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Sql/servers/sql-contoso-prod',
     'Microsoft.Sql/servers', 'Update', DATEADD(HOUR, -14, @Now),
     '[{"path":"properties.publicNetworkAccess","before":"Disabled","after":"Enabled"}]',
     'user', NULL, 'sarah.chen@contoso.com', 'AzurePortal', 'security', 'high'),

    (@TenantId, 'chg-0006',
     'projects/contoso-prod-472019/zones/us-central1-a/instances/gce-contoso-worker-01', 'compute/instance', 'Update', DATEADD(HOUR, -18, @Now),
     '[{"path":"machineType","before":"e2-standard-1","after":"e2-standard-2"}]',
     'user', NULL, 'ext-partner@fabrikam.io', 'AzureCLI', 'cost', 'medium'),

    (@TenantId, 'chg-0007',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Web/sites/app-contoso-api',
     'Microsoft.Web/sites', 'Update', DATEADD(HOUR, -20, @Now),
     '[{"path":"tags.environment","before":"dev","after":"prod"}]',
     'user', NULL, 'james.park@contoso.com', 'AzurePortal', 'governance', 'info'),

    (@TenantId, 'chg-0008',
     'arn:aws:ec2:us-east-1:123456789012:instance/i-0abcd1234efgh5678', 'ec2/instance', 'Update', DATEADD(HOUR, -22, @Now),
     '[{"path":"instanceType","before":"t3.medium","after":"t3.large"}]',
     'servicePrincipal', NULL, 'devops-pipeline-sp', 'AzureCLI', 'cost', 'low');

-- ============================================================================
-- Advisor recommendations
-- ============================================================================
INSERT INTO advisor_recommendations
    (tenant_id, recommendation_id, resource_id, category, impact, title, description, remediation_action,
     estimated_savings, first_seen_at, last_seen_at, lifecycle_status)
VALUES
    (@TenantId, 'rec-cost-001',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Compute/virtualMachines/vm-contoso-app-01',
     'Cost', 'Medium', 'Right-size underutilized virtual machine',
     'CPU utilization has been below 5% for the last 7 days.', 'Resize to a smaller SKU or deallocate.',
     640.00, DATEADD(DAY, -10, @Now), DATEADD(DAY, -1, @Now), 'active'),

    (@TenantId, 'rec-cost-002',
     'arn:aws:ec2:us-east-1:123456789012:instance/i-0abcd1234efgh5678',
     'Cost', 'Low', 'Buy reserved instance to save on compute costs',
     'Consistent usage pattern detected over the last 30 days.', 'Purchase a 1-year reserved instance.',
     820.00, DATEADD(DAY, -20, @Now), DATEADD(DAY, -2, @Now), 'active'),

    (@TenantId, 'rec-sec-001',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Compute/virtualMachines/vm-contoso-web-01',
     'Security', 'High', 'Enable Microsoft Defender for this resource',
     'Advanced threat protection is not enabled for this resource type.', 'Enable the relevant Defender for Cloud plan.',
     NULL, DATEADD(DAY, -15, @Now), DATEADD(DAY, -1, @Now), 'active'),

    (@TenantId, 'rec-rel-001',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Storage/storageAccounts/stcontosoprod001',
     'Reliability', 'Medium', 'Enable soft delete for storage account',
     'Protects against accidental data deletion.', 'Enable blob soft delete with 7+ day retention.',
     NULL, DATEADD(DAY, -30, @Now), DATEADD(DAY, -3, @Now), 'active'),

    (@TenantId, 'rec-perf-001',
     'projects/contoso-prod-472019/instances/contoso-analytics-db',
     'Performance', 'Medium', 'Upgrade to SSD for better IOPS',
     'Standard persistent disk is bottlenecking query performance.', 'Migrate to SSD persistent disk.',
     NULL, DATEADD(DAY, -8, @Now), DATEADD(DAY, -1, @Now), 'active'),

    (@TenantId, 'rec-opex-001',
     'projects/contoso-prod-472019/locations/us-central1/clusters/contoso-gke-prod',
     'OperationalExcellence', 'Low', 'Enable diagnostic logging',
     'This resource is missing a diagnostic setting.', 'Configure diagnostic settings to Log Analytics.',
     NULL, DATEADD(DAY, -25, @Now), DATEADD(DAY, -4, @Now), 'active');

-- ============================================================================
-- Policy compliance (non-compliant)
-- ============================================================================
INSERT INTO policy_compliance
    (tenant_id, policy_assignment_id, policy_definition_id, policy_name, resource_id, compliance_state, category,
     first_seen_at, last_evaluated_at)
VALUES
    (@TenantId, 'pa-001', 'pd-storage-network', 'Storage accounts should restrict network access',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Storage/storageAccounts/stcontosoprod001',
     'NonCompliant', 'Security Center', DATEADD(DAY, -12, @Now), DATEADD(DAY, -1, @Now)),

    (@TenantId, 'pa-002', 'pd-sql-audit', 'SQL servers should have auditing enabled',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Sql/servers/sql-contoso-prod',
     'NonCompliant', 'SQL', DATEADD(DAY, -20, @Now), DATEADD(DAY, -2, @Now)),

    (@TenantId, 'pa-003', 'pd-kv-purge', 'Key vaults should have purge protection enabled',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.KeyVault/vaults/kv-contoso-prod',
     'Compliant', 'Key Vault', DATEADD(DAY, -20, @Now), DATEADD(DAY, -2, @Now)),

    (@TenantId, 'pa-004', 'pd-tags-required', 'Resources should meet required tag policy',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/virtualNetworks/vnet-contoso-hub',
     'NonCompliant', 'Tags', DATEADD(DAY, -18, @Now), DATEADD(DAY, -1, @Now));

-- ============================================================================
-- Defender for Cloud findings
-- ============================================================================
INSERT INTO defender_findings
    (tenant_id, finding_id, resource_id, assessment_name, severity, status, description, remediation_steps,
     first_seen_at, last_seen_at)
VALUES
    (@TenantId, 'df-001',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/networkSecurityGroups/nsg-contoso-web',
     'Management ports should be closed on your virtual machines', 'Critical', 'Unhealthy',
     'Internet-facing management ports like RDP/SSH are open, exposing the VM to brute-force attacks.',
     'Restrict source address to known IP ranges or enable JIT VM access.', DATEADD(DAY, -3, @Now), DATEADD(HOUR, -3, @Now)),

    (@TenantId, 'df-002',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Storage/storageAccounts/stcontosoprod001',
     'Storage accounts should restrict network access', 'High', 'Unhealthy',
     'Public network access is enabled, allowing traffic from any network.',
     'Set network ACLs to Deny with trusted-service exceptions.', DATEADD(DAY, -5, @Now), DATEADD(HOUR, -5, @Now)),

    (@TenantId, 'df-003',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Sql/servers/sql-contoso-prod',
     'Transparent Data Encryption on SQL databases should be enabled', 'High', 'Unhealthy',
     'TDE protects data at rest but is currently disabled.', 'Enable TDE on the database.',
     DATEADD(DAY, -14, @Now), DATEADD(DAY, -1, @Now)),

    (@TenantId, 'df-004',
     'arn:aws:s3:::contoso-prod-assets',
     'Storage accounts should restrict network access', 'High', 'Unhealthy',
     'S3 bucket policy allows public read access.', 'Enable S3 Block Public Access on the bucket.',
     DATEADD(HOUR, -12, @Now), DATEADD(HOUR, -12, @Now)),

    (@TenantId, 'df-005',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Compute/virtualMachines/vm-contoso-web-01',
     'Endpoint protection should be installed', 'Medium', 'Unhealthy',
     'No endpoint protection solution was found on this resource.', 'Install Microsoft Defender for Endpoint.',
     DATEADD(DAY, -9, @Now), DATEADD(DAY, -2, @Now)),

    (@TenantId, 'df-006',
     'projects/contoso-prod-472019/locations/us-central1/clusters/contoso-gke-prod',
     'Diagnostic logs should be enabled', 'Low', 'Unhealthy',
     'Audit logging is not configured for this resource.', 'Enable Cloud Audit Logs for the cluster.',
     DATEADD(DAY, -22, @Now), DATEADD(DAY, -3, @Now));

-- ============================================================================
-- AIOps: anomalies, incidents, gated remediation actions
-- ============================================================================
INSERT INTO anomalies
    (tenant_id, fingerprint, kind, severity, status, provider, title, description, resource_id,
     metric_name, observed_value, baseline_mean, score, detected_at, last_seen_at)
VALUES
    (@TenantId, 'fp-001', 'SecurityPostureRegression', 'Critical', 'Open', 'Azure',
     '3 new severe security finding(s) in 24h (1 critical)',
     'Microsoft Defender for Cloud reported new Critical/High findings in the last 24 hours.',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/networkSecurityGroups/nsg-contoso-web',
     'defender.new_severe.24h', 3, NULL, 3, DATEADD(HOUR, -3, @Now), DATEADD(HOUR, -3, @Now)),

    (@TenantId, 'fp-002', 'ConfigurationDrift', 'High', 'Open', 'Aws',
     'Security configuration drift on contoso-prod-assets',
     '1 security-impacting change detected in 24h. Latest by terraform-automation via AzureCLI.',
     'arn:aws:s3:::contoso-prod-assets', 'changes.security.severe', 1, NULL, NULL, DATEADD(HOUR, -12, @Now), DATEADD(HOUR, -12, @Now)),

    (@TenantId, 'fp-003', 'CostAnomaly', 'Medium', 'Acknowledged', 'Gcp',
     'Identified waste jumped to $2,400/yr (baseline $1,100/yr)',
     'Estimated annual cost savings rose sharply, usually meaning newly over-provisioned resources.',
     'projects/contoso-prod-472019/zones/us-central1-a/instances/gce-contoso-worker-01',
     'advisor.cost.savings', 2400, 1100, 2.18, DATEADD(HOUR, -18, @Now), DATEADD(HOUR, -18, @Now)),

    (@TenantId, 'fp-004', 'UnusualActorActivity', 'High', 'Open', 'Azure',
     'First-seen actor ''ext-partner@fabrikam.io'' made 1 change(s)',
     'Actor has no activity in the 30-day baseline. Verify this identity is expected.',
     'projects/contoso-prod-472019/zones/us-central1-a/instances/gce-contoso-worker-01',
     'changes.actor.new', 1, NULL, NULL, DATEADD(HOUR, -18, @Now), DATEADD(HOUR, -18, @Now));

INSERT INTO incidents
    (tenant_id, title, severity, status, source, summary_markdown, created_at, sla_due_at)
VALUES
    (@TenantId, '3 new severe security finding(s) in 24h (1 critical)', 'Critical', 'Open', 'anomaly',
     'Microsoft Defender for Cloud reported new Critical/High findings in the last 24 hours.',
     DATEADD(HOUR, -3, @Now), DATEADD(HOUR, 1, @Now)),

    (@TenantId, 'Security configuration drift on contoso-prod-assets', 'High', 'Acknowledged', 'anomaly',
     '1 security-impacting change detected in 24h.', DATEADD(HOUR, -12, @Now), DATEADD(HOUR, -4, @Now));

UPDATE anomalies SET incident_id = (SELECT id FROM incidents WHERE tenant_id = @TenantId AND title = '3 new severe security finding(s) in 24h (1 critical)')
WHERE tenant_id = @TenantId AND fingerprint = 'fp-001';

UPDATE anomalies SET incident_id = (SELECT id FROM incidents WHERE tenant_id = @TenantId AND title = 'Security configuration drift on contoso-prod-assets')
WHERE tenant_id = @TenantId AND fingerprint = 'fp-002';

INSERT INTO remediation_actions
    (tenant_id, playbook_key, provider, resource_id, title, reason, parameters_json, status, risk_level,
     requested_by, anomaly_id, incident_id, approval_mode, created_at, expires_at)
VALUES
    (@TenantId, 'azure-nsg-remove-rule', 'Azure',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-shared-network/providers/Microsoft.Network/networkSecurityGroups/nsg-contoso-web',
     'Remove NSG security rule',
     'NSG rule opened RDP to the internet, detected as configuration drift.',
     '{"ruleName":"AllowRDP"}', 'PendingApproval', 'High', 'AI Operations Agent',
     (SELECT id FROM anomalies WHERE tenant_id = @TenantId AND fingerprint = 'fp-001'),
     (SELECT id FROM incidents WHERE tenant_id = @TenantId AND title = '3 new severe security finding(s) in 24h (1 critical)'),
     'gated', DATEADD(HOUR, -2, @Now), DATEADD(HOUR, 22, @Now)),

    (@TenantId, 'aws-s3-block-public-access', 'Aws', 'arn:aws:s3:::contoso-prod-assets',
     'Block S3 public access', 'S3 bucket policy allows public read access, flagged by configuration drift detection.',
     '{"bucket":"contoso-prod-assets","region":"us-east-1"}', 'PendingApproval', 'Low', 'AI Operations Agent',
     (SELECT id FROM anomalies WHERE tenant_id = @TenantId AND fingerprint = 'fp-002'),
     (SELECT id FROM incidents WHERE tenant_id = @TenantId AND title = 'Security configuration drift on contoso-prod-assets'),
     'gated', DATEADD(HOUR, -11, @Now), DATEADD(HOUR, 13, @Now)),

    (@TenantId, 'azure-storage-require-https', 'Azure',
     '/subscriptions/D0000000-0000-0000-0000-000000000001/resourceGroups/rg-prod-eastus/providers/Microsoft.Storage/storageAccounts/stcontosoprod001',
     'Require HTTPS-only transport', 'Storage account allows plain HTTP transport.',
     NULL, 'Succeeded', 'Low', 'james.park@contoso.com', NULL, NULL, 'gated',
     DATEADD(DAY, -2, @Now), NULL);

UPDATE remediation_actions SET
    approved_by = 'james.park@contoso.com', approved_at = DATEADD(DAY, -2, DATEADD(MINUTE, 5, @Now)),
    executed_at = DATEADD(DAY, -2, DATEADD(MINUTE, 5, @Now)), completed_at = DATEADD(DAY, -2, DATEADD(MINUTE, 6, @Now)),
    result_json = '{"success":true}'
WHERE tenant_id = @TenantId AND playbook_key = 'azure-storage-require-https';

PRINT 'Seeded Contoso Corp with Azure + AWS + GCP demo data.';
