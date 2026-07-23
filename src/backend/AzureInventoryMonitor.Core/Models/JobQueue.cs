namespace AzureInventoryMonitor.Core.Models;

/// <summary>A single dequeued job, backed by either Service Bus or the database queue table.</summary>
public sealed class QueuedJob
{
    public long Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public int Attempts { get; set; }
}
