-- ============================================================================
-- Migration 004: Local username/password authentication
--
-- Adds an alternative login path to Entra ID SSO so the platform can run
-- without an Entra tenant (local dev, or self-hosting on any cloud). A user
-- now authenticates via EITHER Entra ID (user_id = Entra object ID) OR a
-- local username/password (user_id = a generated GUID). auth_provider marks
-- which one applies to a given row.
--
-- Password hash format: pbkdf2$sha256$<iterations>$<saltBase64>$<hashBase64>
-- (see AzureInventoryMonitor.Core/Auth/PasswordHasher.cs for the reader/writer).
--
-- Run after 001-schema.sql, 002-fix-rls-bypass.sql, 003-aiops-multicloud.sql.
-- ============================================================================

ALTER TABLE users ADD
    auth_provider   NVARCHAR(20)    NOT NULL
        CONSTRAINT DF_users_auth_provider DEFAULT 'entra'
        CHECK (auth_provider IN ('entra', 'local')),
    username        NVARCHAR(128)   NULL,
    password_hash   NVARCHAR(256)   NULL;
GO

-- Only local accounts have a username, and it must be unique among them.
-- Filtered indexes require QUOTED_IDENTIFIER ON — sqlcmd defaults it OFF
-- (unlike SSMS), so set it explicitly for this batch.
SET QUOTED_IDENTIFIER ON;
GO
CREATE UNIQUE INDEX UX_users_username ON users(username) WHERE username IS NOT NULL;
GO

-- ============================================================================
-- Seed a default local admin so a fresh local/self-hosted deployment has a
-- working login immediately.
--
--   username: admin
--   password: ChangeMe123!   <-- DEV DEFAULT. Change this immediately outside
--                                of local/dev use; this hash is public (it's
--                                in source control).
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM users WHERE username = 'admin')
BEGIN
    INSERT INTO users (user_id, display_name, email, global_role, is_active, auth_provider, username, password_hash)
    VALUES (
        'a1000000-0000-0000-0000-000000000001',
        'Local Admin',
        'admin@local',
        'admin',
        1,
        'local',
        'admin',
        'pbkdf2$sha256$210000$2gNPz+6njzR/uNEO1g3o9A==$zaisf4nCNps9iP/VJ++Io6KzgyPXL2FEzg4Ux22FYpE='
    );
END
GO
