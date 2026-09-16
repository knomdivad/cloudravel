-- ============================================================================
-- 003-drop-assessment.sql — remove the in-app assessment engagement
--
-- The fixed-fee assessment moved out of CloudRavel into the standalone
-- cloudravel-assessment tool (its own engagement store, reads CloudRavel
-- data via the read-only service-credential API path). The app is the
-- ongoing cost-advising/monitoring product and no longer carries any
-- assessment concept, so the tenants engagement columns go.
--
-- Idempotent: every statement re-runs safely (drops are conditional).
-- Fresh installs simply never have these columns (001-schema has no
-- assessment columns anymore).
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tenants_engagement'
           AND object_id = OBJECT_ID('dbo.tenants'))
    DROP INDEX IX_tenants_engagement ON dbo.tenants;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_completed_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP CONSTRAINT DF_tenants_engagement_kind;
GO

-- Drop in dependency order (constraint first via the guard above, then columns).
IF COL_LENGTH('dbo.tenants', 'assessment_completed_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN assessment_completed_at;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_ends_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN assessment_ends_at;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_started_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN assessment_started_at;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.tenants')
           AND name = 'engagement_kind')
BEGIN
    -- Re-create a covering index replacement is unnecessary; drop the column.
    ALTER TABLE dbo.tenants DROP COLUMN engagement_kind;
END;
GO
