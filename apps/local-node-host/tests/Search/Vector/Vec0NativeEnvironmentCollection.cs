using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

internal static class Vec0NativeEnvironmentCollection
{
    internal const string Name = "vec0 native process environment";
}

/// <summary>
/// Serializes tests that mutate or assert the process-global vec0 opt-in variables. A build-staged native makes
/// the old timing accident observable: a parity test's temporary opt-in can otherwise leak into composition.
/// </summary>
[CollectionDefinition(Vec0NativeEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class Vec0NativeEnvironmentCollectionDefinition;
