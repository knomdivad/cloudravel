-- Customer-chat abuse caps (rate windows + daily token usage).
-- Matches SqlChatUsageStore (counter_key, window_start, counter_count, updated_at).
-- Not tenant-scoped (keys embed tenant ids as opaque strings); no RLS, same as 001-schema.
-- Idempotent for volumes that applied 001-schema before this table existed.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.chat_usage_counters', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.chat_usage_counters (
        counter_key   NVARCHAR(200) NOT NULL PRIMARY KEY,
        window_start  DATETIME2     NOT NULL,
        counter_count BIGINT        NOT NULL DEFAULT 0,
        updated_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO
