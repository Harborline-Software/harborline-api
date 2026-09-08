using Harborline.Api.Blocks.AccessGrant;

/// <summary>Builds all in-memory authorization test stores around one universal fence.</summary>
internal static class TestInMemoryAuthorizationStores
{
    private static readonly InMemoryAuthorizationBootstrapFence SharedFence = new();

    internal static InMemoryGrantStore GrantStore() => new(SharedFence);

    internal static InMemoryAuthorizationConfigurationStore ConfigurationStore() => new(SharedFence);

    internal static (InMemoryGrantStore Grants, InMemoryAuthorizationConfigurationStore Configuration) Pair() =>
        (new InMemoryGrantStore(SharedFence), new InMemoryAuthorizationConfigurationStore(SharedFence));
}
