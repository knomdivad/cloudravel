using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;

namespace AzureInventoryMonitor.Infrastructure.MultiCloud;

/// <summary>
/// Resolves the registered adapter for a cloud provider.
/// Adapters are registered in DI as ICloudProviderAdapter (one per provider).
/// </summary>
public sealed class CloudProviderAdapterFactory : ICloudProviderAdapterFactory
{
    private readonly Dictionary<CloudProvider, ICloudProviderAdapter> _adapters;

    public CloudProviderAdapterFactory(IEnumerable<ICloudProviderAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(a => a.Provider);
    }

    public ICloudProviderAdapter GetAdapter(CloudProvider provider)
    {
        if (_adapters.TryGetValue(provider, out var adapter))
            return adapter;
        throw new NotSupportedException($"No adapter registered for cloud provider '{provider}'.");
    }
}
