using Dapper;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;

namespace AzureInventoryMonitor.Infrastructure.Queue;

/// <summary>
/// SQL-table-backed job queue (see database/005-job-queue.sql). The default
/// <see cref="IJobQueue"/> implementation when no Service Bus connection is
/// configured — needs nothing beyond the database every deployment already
/// requires, so it works identically locally, on any cloud, or on-prem.
///
/// Dequeue uses UPDLOCK+READPAST so concurrent pollers never grab the same row.
/// </summary>
public sealed class DatabaseJobQueue : IJobQueue
{
    private readonly ITenantDbConnectionFactory _connectionFactory;

    public DatabaseJobQueue(ITenantDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnqueueAsync(string queueName, string payloadJson, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO job_queue (queue_name, payload_json) VALUES (@QueueName, @PayloadJson)",
            new { QueueName = queueName, PayloadJson = payloadJson });
    }

    public async Task<IReadOnlyList<QueuedJob>> DequeueBatchAsync(string queueName, int maxCount, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            UPDATE job_queue SET dequeued_at = SYSUTCDATETIME()
            OUTPUT inserted.id AS Id, inserted.queue_name AS QueueName,
                   inserted.payload_json AS PayloadJson, inserted.attempts AS Attempts
            WHERE id IN (
                SELECT TOP (@MaxCount) id FROM job_queue WITH (READPAST, UPDLOCK)
                WHERE queue_name = @QueueName AND processed_at IS NULL AND dequeued_at IS NULL
                      AND available_at <= SYSUTCDATETIME()
                ORDER BY id
            )";

        var results = await conn.QueryAsync<QueuedJob>(sql, new { QueueName = queueName, MaxCount = maxCount });
        return results.ToList();
    }

    public async Task CompleteAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync("UPDATE job_queue SET processed_at = SYSUTCDATETIME() WHERE id = @Id", new { job.Id });
    }

    public async Task FailAsync(QueuedJob job, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE job_queue SET dequeued_at = NULL, attempts = attempts + 1,
                   available_at = DATEADD(SECOND, @DelaySeconds, SYSUTCDATETIME()), error = @Error
            WHERE id = @Id",
            new { job.Id, Error = error, DelaySeconds = (int)retryDelay.TotalSeconds });
    }
}
