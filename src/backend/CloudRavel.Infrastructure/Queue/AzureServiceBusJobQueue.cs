using Azure.Messaging.ServiceBus;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Configuration;

namespace CloudRavel.Infrastructure.Queue;

/// <summary>
/// Azure Service Bus-backed job queue — used when a Service Bus connection IS
/// configured, preserving today's production Azure behavior. Uses the SDK
/// programmatically (not a trigger binding), so it's just one interchangeable
/// <see cref="IJobQueue"/> implementation rather than a startup-time dependency.
/// Scoped lifetime: dequeue and complete/fail happen within the same
/// invocation, so in-flight messages only need to be tracked for that long.
/// </summary>
public sealed class AzureServiceBusJobQueue : IJobQueue, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly Dictionary<long, (ServiceBusReceiver Receiver, ServiceBusReceivedMessage Message)> _inFlight = new();

    public AzureServiceBusJobQueue(IConfiguration config)
    {
        var connectionString = config["ServiceBusConnection"]
            ?? config.GetConnectionString("ServiceBusConnection")
            ?? throw new InvalidOperationException("ServiceBusConnection is not configured.");
        _client = new ServiceBusClient(connectionString);
    }

    public async Task EnqueueAsync(string queueName, string payloadJson, CancellationToken cancellationToken = default)
    {
        await using var sender = _client.CreateSender(queueName);
        await sender.SendMessageAsync(new ServiceBusMessage(payloadJson), cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedJob>> DequeueBatchAsync(string queueName, int maxCount, CancellationToken cancellationToken = default)
    {
        var receiver = _client.CreateReceiver(queueName);
        var messages = await receiver.ReceiveMessagesAsync(maxCount, TimeSpan.FromSeconds(5), cancellationToken);

        var jobs = new List<QueuedJob>();
        foreach (var message in messages)
        {
            var job = new QueuedJob
            {
                Id = message.SequenceNumber,
                QueueName = queueName,
                PayloadJson = message.Body.ToString(),
                Attempts = message.DeliveryCount,
            };
            _inFlight[job.Id] = (receiver, message);
            jobs.Add(job);
        }
        return jobs;
    }

    public async Task CompleteAsync(QueuedJob job, CancellationToken cancellationToken = default)
    {
        if (_inFlight.Remove(job.Id, out var entry))
            await entry.Receiver.CompleteMessageAsync(entry.Message, cancellationToken);
    }

    public async Task FailAsync(QueuedJob job, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default)
    {
        // Service Bus manages its own redelivery/backoff via lock duration and
        // max delivery count; abandoning makes it available again immediately.
        if (_inFlight.Remove(job.Id, out var entry))
            await entry.Receiver.AbandonMessageAsync(entry.Message, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (receiver, _) in _inFlight.Values)
            await receiver.DisposeAsync();
        await _client.DisposeAsync();
    }
}
