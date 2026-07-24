using CloudRavel.Core.Models;

namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Cloud-agnostic background job queue. <c>AzureServiceBusJobQueue</c> is used
/// when a Service Bus connection is configured (the default on Azure);
/// <c>DatabaseJobQueue</c> is the default otherwise — a SQL-table-backed
/// queue that needs no infrastructure beyond the database every deployment
/// already requires, so the Functions host never hard-depends on Service Bus
/// just to start. Mirrors the existing <see cref="ICloudProviderAdapter"/>
/// pattern: one interface, interchangeable implementations chosen by config.
/// </summary>
public interface IJobQueue
{
    Task EnqueueAsync(string queueName, string payloadJson, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueuedJob>> DequeueBatchAsync(string queueName, int maxCount, CancellationToken cancellationToken = default);

    Task CompleteAsync(QueuedJob job, CancellationToken cancellationToken = default);

    Task FailAsync(QueuedJob job, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default);
}
