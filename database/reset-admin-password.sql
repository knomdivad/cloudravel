-- ============================================================================
-- Reset bootstrap local admin password to ChangeMe123!
-- Use when login fails after upgrades / partial migrations.
--
-- sqlcmd -S localhost -d cloudraveldb -U sa -P "$MSSQL_SA_PASSWORD" -I \
--   -i database/reset-admin-password.sql
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Ensure the admin row exists and is active system_admin with known password hash
-- (pbkdf2 sha256 210000 — password ChangeMe123!).
IF NOT EXISTS (SELECT 1 FROM users WHERE username = 'admin' AND auth_provider = 'local')
BEGIN
    INSERT INTO users (user_id, display_name, email, global_role, is_active, auth_provider, username, password_hash)
    VALUES (
        'a1000000-0000-0000-0000-000000000001',
        'Local Admin',
        'admin@local',
        'system_admin',
        1,
        'local',
        'admin',
        'pbkdf2$sha256$210000$2gNPz+6njzR/uNEO1g3o9A==$zaisf4nCNps9iP/VJ++Io6KzgyPXL2FEzg4Ux22FYpE='
    );
    PRINT 'Inserted bootstrap admin user.';
END
ELSE
BEGIN
    UPDATE users SET
        password_hash = 'pbkdf2$sha256$210000$2gNPz+6njzR/uNEO1g3o9A==$zaisf4nCNps9iP/VJ++Io6KzgyPXL2FEzg4Ux22FYpE=',
        is_active = 1,
        global_role = 'system_admin',
        auth_provider = 'local'
    WHERE username = 'admin' AND auth_provider = 'local';
    PRINT 'Reset admin password and role.';
END
GO

SELECT user_id, username, global_role, is_active, auth_provider,
       LEFT(password_hash, 30) AS password_hash_prefix
FROM users
WHERE username = 'admin';
GO
