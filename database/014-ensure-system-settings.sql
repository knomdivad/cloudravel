-- ============================================================================
-- Migration 014: Ensure system_settings + org_sso_settings exist
--
-- 010-admin-rbac introduces these tables, but some long-lived volumes have
-- 010 marked applied in dbo.__migrations without the tables (e.g. 010 was
-- expanded after first apply, or a partial run was ledgered). AI Admin settings
-- then 500 with "Invalid object name 'system_settings'".
--
-- Idempotent: safe if 010 already created them correctly.
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.system_settings', N'U') IS NULL
BEGIN
    CREATE TABLE system_settings (
        setting_key   NVARCHAR(128) NOT NULL PRIMARY KEY,
        setting_value NVARCHAR(MAX) NULL,
        updated_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_by    NVARCHAR(256) NOT NULL DEFAULT 'system'
    );
END
GO

IF OBJECT_ID(N'dbo.org_sso_settings', N'U') IS NULL
BEGIN
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
END
GO
