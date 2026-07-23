namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Instance-level platform settings, independent of any cloud provider.
/// The <see cref="Environment"/> gates real cloud calls: in Development the
/// internal schedulers (and on-demand triggers) never collect inventory, so a
/// demo/seed instance won't reach out to fake clouds.
/// </summary>
public interface IPlatformInfo
{
    /// <summary>"Development" (default) or "Production".</summary>
    string Environment { get; }

    /// <summary>True only when Environment == "Production".</summary>
    bool IsProduction { get; }
}
