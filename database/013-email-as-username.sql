-- ============================================================================
-- Migration 013: Email is the unique login identity
--
-- - email is required and unique (case-normalized to lower on write)
-- - local username is the same as email (SSO/Entra identity is also email-based)
-- - remediates blank emails and aligns bootstrap admin to admin@local
--
-- Run after 001-012.
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Fill blank emails so the unique index can be created.
UPDATE users
SET email = LOWER(COALESCE(NULLIF(LTRIM(RTRIM(username)), ''), CAST(user_id AS NVARCHAR(36))) + '@local')
WHERE email IS NULL OR LTRIM(RTRIM(email)) = '';
GO

UPDATE users SET email = LOWER(LTRIM(RTRIM(email)));
GO

-- Bootstrap local admin: login identity is the email.
UPDATE users
SET username = 'admin@local',
    email = 'admin@local',
    display_name = COALESCE(NULLIF(LTRIM(RTRIM(display_name)), ''), 'Local Admin')
WHERE auth_provider = 'local'
  AND (username = 'admin' OR email IN ('admin@local', 'admin'));
GO

-- Deduplicate emails before unique index (keep oldest row per address).
;WITH ranked AS (
    SELECT user_id,
           ROW_NUMBER() OVER (PARTITION BY email ORDER BY created_at, user_id) AS rn
    FROM users
)
UPDATE u
SET email = u.email + '+dup-' + LOWER(REPLACE(CAST(u.user_id AS NVARCHAR(36)), '-', ''))
FROM users u
INNER JOIN ranked r ON r.user_id = u.user_id AND r.rn > 1;
GO

-- Local accounts: username mirrors email (login + SSO-aligned identity).
UPDATE users
SET username = email
WHERE auth_provider = 'local'
  AND (username IS NULL OR LTRIM(RTRIM(username)) = '' OR username <> email);
GO

-- Unique email (case already normalized to lower).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_users_email' AND object_id = OBJECT_ID('dbo.users'))
BEGIN
    CREATE UNIQUE INDEX UX_users_email ON users(email);
END
GO

