namespace CloudRavel.Core.Auth;

/// <summary>Feature-set flags for a user within one organization.</summary>
public sealed record FeatureEntitlements(bool BotMonitoring, bool CloudMonitoring)
{
    public static readonly FeatureEntitlements None = new(false, false);
    public static readonly FeatureEntitlements Both = new(true, true);

    public static FeatureEntitlements IntersectWithOrg(FeatureEntitlements member, FeatureEntitlements org)
        => new(member.BotMonitoring && org.BotMonitoring, member.CloudMonitoring && org.CloudMonitoring);
}

public static class FeatureEntitlementKeys
{
    public const string BotMonitoring = "EntitlementBotMonitoring";
    public const string CloudMonitoring = "EntitlementCloudMonitoring";
}
