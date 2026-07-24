-- ============================================================================
-- Migration 005: Cloud-agnostic job queue
--
-- Backs IJobQueue's DatabaseJobQueue implementation — a SQL-table-based
-- outbox/queue used when no Azure Service Bus connection is configured (local
-- dev, or self-hosting on a non-Azure cloud). Needs no infrastructure beyond
-- the SQL database every deployment already requires. AzureServiceBusJobQueue
-- remains the default when ServiceBusConnection IS configured; this table is
-- simply unused in that case.
--
-- Run after 001-schema.sql through 004-local-auth.sql.
-- ============================================================================

CREATE TABLE job_queue (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    queue_name      NVARCHAR(100)   NOT NULL,
    payload_json    NVARCHAR(MAX)   NOT NULL,
    created_at      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    available_at    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),  -- supports delayed retry
    dequeued_at     DATETIME2       NULL,
    processed_at    DATETIME2       NULL,
    attempts        INT             NOT NULL DEFAULT 0,
    error           NVARCHAR(2000)  NULL,
    INDEX IX_job_queue_pending (queue_name, available_at) INCLUDE (processed_at)
);
GO
