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
-- Constraint names differ between fresh installs (named DF constraint in
-- 001) and upgraded volumes (system-named from 002's ALTER TABLE), so the
-- CHECK constraint is dropped dynamically by column, not by name.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tenants_engagement'
           AND object_id = OBJECT_ID('dbo.tenants'))
    DROP INDEX IX_tenants_engagement ON dbo.tenants;
GO

-- Drop every CHECK constraint on engagement_kind, whatever it is named.
DECLARE @drop_sql NVARCHAR(MAX) =
    (SELECT STRING_AGG('ALTER TABLE dbo.tenants DROP CONSTRAINT ' + QUOTENAME(name), '; ')
     FROM sys.check_constraints
     WHERE parent_object_id = OBJECT_ID('dbo.tenants')
       AND parent_column_id = COLUMNPROPERTY(OBJECT_ID('dbo.tenants'), 'engagement_kind', 'ColumnId'));
IF @drop_sql IS NOT NULL EXEC(@drop_sql);
GO

IF COL_LENGTH('dbo.tenants', 'assessment_completed_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN assessment_completed_at;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_ends_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN assessment_ends_at;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_started_at') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN assessment_started_at;
GO

IF COL_LENGTH('dbo.tenants', 'engagement_kind') IS NOT NULL
    ALTER TABLE dbo.tenants DROP COLUMN engagement_kind;
GO
