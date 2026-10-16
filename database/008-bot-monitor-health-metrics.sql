-- Optional self-reported health metrics on heartbeats (sanitized JSON, size capped at app layer).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo.bot_monitor_heartbeats', N'reported_metrics') IS NULL
BEGIN
    ALTER TABLE bot_monitor_heartbeats
        ADD reported_metrics NVARCHAR(4000) NULL;
END
GO
