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

-- Login identity is email: admin@local / ChangeMe123!
IF NOT EXISTS (SELECT 1 FROM users WHERE auth_provider = 'local' AND (username = 'admin@local' OR email = 'admin@local' OR username = 'admin'))
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
    PRINT 'Inserted bootstrap admin user (admin@local).';
END
ELSE
BEGIN
    UPDATE users SET
        username = 'admin@local',
        email = 'admin@local',
        password_hash = 'pbkdf2$sha256$210000$2gNPz+6njzR/uNEO1g3o9A==$zaisf4nCNps9iP/VJ++Io6KzgyPXL2FEzg4Ux22FYpE=',
        is_active = 1,
        global_role = 'system_admin',
        auth_provider = 'local'
    WHERE auth_provider = 'local'
      AND (username IN ('admin', 'admin@local') OR email IN ('admin@local', 'admin'));
    PRINT 'Reset admin@local password and role.';
END
GO

SELECT user_id, username, email, global_role, is_active, auth_provider,
       LEFT(password_hash, 30) AS password_hash_prefix
FROM users
WHERE email = 'admin@local' OR username IN ('admin', 'admin@local');
GO
