using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Serializes tests that mutate the process-wide <c>HARBORLINE_PQC_HYBRID_WRITE_DISABLED</c> variable.
/// Both HybridKem test classes use <c>NodeHybridKemWritePolicyComposition.DisableEnvVarName</c>;
/// their per-class save/restore cannot prevent cross-class interleaving under xUnit's default parallelism.
/// Keeping them in one non-parallel collection prevents either class, or another collection, from observing
/// the variable while the kill-switch state is temporarily changed.
/// </summary>
[CollectionDefinition("Hybrid KEM write policy environment", DisableParallelization = true)]
public sealed class HybridKemEnvironmentCollection
{
}
