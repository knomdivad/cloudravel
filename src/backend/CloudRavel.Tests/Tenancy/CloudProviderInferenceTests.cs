using CloudRavel.Core.Models;
using Xunit;

namespace CloudRavel.Tests.Tenancy;

public sealed class CloudProviderInferenceTests
{
    [Theory]
    [InlineData("//compute.googleapis.com/projects/p/zones/z/instances/i", null, CloudProvider.Gcp)]
    [InlineData("arn:aws:s3:::my-bucket", null, CloudProvider.Aws)]
    [InlineData("/subscriptions/xxx/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/a", null, CloudProvider.Azure)]
    [InlineData("gcp-scc:abc", null, CloudProvider.Gcp)]
    [InlineData("aws-s3-pab:123:bucket", null, CloudProvider.Aws)]
    [InlineData(null, "gcp", CloudProvider.Gcp)]
    [InlineData(null, "Aws", CloudProvider.Aws)]
    public void FromResource_infers_provider(string? resourceId, string? hint, CloudProvider expected)
    {
        Assert.Equal(expected, CloudProviderInference.FromResource(resourceId, hint));
    }

    [Fact]
    public void FromResources_majority_gcp()
    {
        var ids = new[]
        {
            "//storage.googleapis.com/projects/p/buckets/b1",
            "//storage.googleapis.com/projects/p/buckets/b2",
            "arn:aws:s3:::other"
        };
        Assert.Equal(CloudProvider.Gcp, CloudProviderInference.FromResources(ids));
    }
}
