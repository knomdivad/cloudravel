-- ============================================================================
-- 002-assessment.sql — 10-day fixed-fee assessment engagement support
--
-- Adds the assessment engagement lifecycle to the workspace tenants row:
--   engagement_kind      'standard' (default) | 'assessment'
--   assessment_started_at   when the assessment window opened
--   assessment_ends_at      started_at + configured watch window (default 10d)
--   assessment_completed_at when the assessment was closed out
--
-- An assessment workspace is time-boxed and read-only in effect: the API forces
-- AutoRemediationMode='disabled' on start and the remediation engine refuses
-- approval/execution while engagement_kind='assessment' (see AssessmentPolicy).
-- Converting to an ongoing engagement restores the previous approval mode.
--
-- Idempotent: every statement re-runs safely against an upgraded volume.
-- Fresh installs also get these columns via 001-schema.sql.
-- ============================================================================

-- --- tenants: engagement lifecycle columns -----------------------------------

IF COL_LENGTH('dbo.tenants', 'engagement_kind') IS NULL
    ALTER TABLE dbo.tenants
        ADD engagement_kind NVARCHAR(20) NOT NULL
            CONSTRAINT DF_tenants_engagement_kind DEFAULT 'standard'
            CHECK (engagement_kind IN ('standard', 'assessment'));
GO

IF COL_LENGTH('dbo.tenants', 'assessment_started_at') IS NULL
    ALTER TABLE dbo.tenants ADD assessment_started_at DATETIME2 NULL;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_ends_at') IS NULL
    ALTER TABLE dbo.tenants ADD assessment_ends_at DATETIME2 NULL;
GO

IF COL_LENGTH('dbo.tenants', 'assessment_completed_at') IS NULL
    ALTER TABLE dbo.tenants ADD assessment_completed_at DATETIME2 NULL;
GO

-- Assessment endpoints filter on kind + state.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tenants_engagement')
    CREATE INDEX IX_tenants_engagement ON dbo.tenants (engagement_kind, assessment_started_at);
GO
