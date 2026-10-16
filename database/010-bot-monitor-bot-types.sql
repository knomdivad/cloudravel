-- Bot type lookup (extensible; separate from platform transport field).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.bot_monitor_bot_types', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.bot_monitor_bot_types (
        type_key      NVARCHAR(64)  NOT NULL PRIMARY KEY,
        display_name  NVARCHAR(128) NOT NULL,
        is_active     BIT           NOT NULL CONSTRAINT DF_bmbt_active DEFAULT 1,
        created_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF COL_LENGTH(N'dbo.bot_monitor_bots', N'bot_type_key') IS NULL
BEGIN
    ALTER TABLE dbo.bot_monitor_bots
        ADD bot_type_key NVARCHAR(64) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_bmb_bot_type' AND object_id = OBJECT_ID(N'dbo.bot_monitor_bots'))
    CREATE INDEX IX_bmb_bot_type ON dbo.bot_monitor_bots(tenant_id, bot_type_key);
GO

MERGE dbo.bot_monitor_bot_types AS t
USING (VALUES
    (N'grokbot', N'Grokbot', 1),
    (N'chatgpt', N'ChatGPT', 1),
    (N'muse_meta', N'Muse/Meta', 1),
    (N'openclaw', N'OpenClaw', 1),
    (N'hermes', N'Hermes', 1),
    (N'dot', N'Dot', 1),
    (N'other_plugin', N'Other plugin', 1)
) AS s(type_key, display_name, is_active)
ON t.type_key = s.type_key
WHEN NOT MATCHED BY TARGET THEN
    INSERT (type_key, display_name, is_active) VALUES (s.type_key, s.display_name, s.is_active);
GO

UPDATE dbo.bot_monitor_bots
SET bot_type_key = N'other_plugin'
WHERE bot_type_key IS NULL;
GO

IF EXISTS (SELECT 1 FROM dbo.bot_monitor_bots WHERE bot_type_key IS NULL)
    ALTER TABLE dbo.bot_monitor_bots ALTER COLUMN bot_type_key NVARCHAR(64) NOT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_bot_monitor_bots_bot_type')
BEGIN
    ALTER TABLE dbo.bot_monitor_bots
        ADD CONSTRAINT FK_bot_monitor_bots_bot_type
            FOREIGN KEY (bot_type_key) REFERENCES dbo.bot_monitor_bot_types(type_key);
END
GO
