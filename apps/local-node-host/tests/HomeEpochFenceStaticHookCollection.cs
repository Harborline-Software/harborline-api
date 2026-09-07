using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests;

/// <summary>
/// Serializes the test classes that share the PROCESS-GLOBAL static test hook
/// <c>HomeEpochFence.AfterReadHookForTests</c> —
/// <see cref="ArchTests.HomeEpochFenceAtomicityArchTests"/>, <see cref="Entities.HomeEpochFenceTests"/>,
/// <see cref="AssetRegistry.SpatialFrameDescriptorMintTests"/>,
/// <see cref="AssetRegistry.SpatialFramePiiSealingTests"/> and
/// <see cref="AssetRegistry.SpatialFrameRouteTests"/> (whose fenced mints enter
/// <c>HomeEpochWriteScope</c> and run <c>AssertNotStaleAsync</c>, which FIRES the hook — so a hook
/// installed concurrently by a fence test would interleave a foreign promotion into a spatial mint,
/// and vice versa).
/// <para>
/// xUnit runs distinct test classes in PARALLEL by default (each class is its own implicit collection).
/// Both classes above set/clear that ONE static hook around a fenced write, so when they run concurrently
/// they race on it: one class's hook — or its <c>finally</c> resetting it to <see langword="null"/> — leaks
/// into the other class's fenced write. That intermittently corrupts the behavioural G-4 assertion
/// (<c>concurrentWriterWasBlocked == false</c>) and the interleaved-promotion assertions
/// (<c>promotionAttemptThrew == false</c>), which is the CI flake this collection closes.
/// </para>
/// <para>
/// Putting BOTH classes in ONE named collection makes xUnit run their tests SEQUENTIALLY, so the static
/// hook is only ever installed by one test at a time — the cross-test data race is eliminated. This changes
/// NOTHING about the fence invariant the tests protect (the fence code, the BEGIN IMMEDIATE lock, and every
/// assertion are untouched); it only removes the shared-static race on the test-only interleave hook. The
/// collection is intentionally NOT <c>DisableParallelization</c>: only these two classes touch the static,
/// so it still runs in parallel with unrelated collections — no whole-suite slowdown.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class HomeEpochFenceStaticHookCollection
{
    public const string Name = "HomeEpochFence static hook (serialized)";
}
