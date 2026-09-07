using Harborline.Api.Foundation.LocalFirst;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Foundation.LocalFirst.Tests;

public sealed class CausalConflictResolverTests
{
    private static readonly byte[] Local = { 1 };
    private static readonly byte[] Remote = { 2 };

    private static SyncConflict Conflict(
        IReadOnlyDictionary<string, ulong> localClock,
        IReadOnlyDictionary<string, ulong> remoteClock,
        DateTimeOffset? localAt = null,
        DateTimeOffset? remoteAt = null) => new()
    {
        Key = "k",
        LocalVersion = Local,
        RemoteVersion = Remote,
        LocalClock = localClock,
        RemoteClock = remoteClock,
        LocalModifiedAt = localAt,
        RemoteModifiedAt = remoteAt,
    };

    private static byte[] ResolvedPayload(ConflictResolution resolution) =>
        Assert.IsType<ConflictResolution.Resolved>(resolution).Payload;

    [Fact]
    public async Task Older_wallclock_local_edit_that_causally_dominates_wins()
    {
        // The ticket-149 defect: under LastWriterWins the remote's later
        // wall-clock timestamp silently discarded this local edit, even though
        // the local writer had already seen the remote version (its clock
        // dominates). Causality, not timestamps, must decide.
        var conflict = Conflict(
            localClock: new Dictionary<string, ulong> { ["A"] = 2, ["B"] = 1 },
            remoteClock: new Dictionary<string, ulong> { ["A"] = 1, ["B"] = 1 },
            localAt: DateTimeOffset.UtcNow.AddMinutes(-10), // device clock behind
            remoteAt: DateTimeOffset.UtcNow);

        var resolution = await new CausalConflictResolver().ResolveAsync(conflict);

        Assert.Equal(Local, ResolvedPayload(resolution));
    }

    [Fact]
    public async Task Remote_that_causally_dominates_wins()
    {
        var conflict = Conflict(
            localClock: new Dictionary<string, ulong> { ["A"] = 1 },
            remoteClock: new Dictionary<string, ulong> { ["A"] = 1, ["B"] = 3 });

        var resolution = await new CausalConflictResolver().ResolveAsync(conflict);

        Assert.Equal(Remote, ResolvedPayload(resolution));
    }

    [Fact]
    public async Task Concurrent_edits_are_surfaced_not_picked()
    {
        var conflict = Conflict(
            localClock: new Dictionary<string, ulong> { ["A"] = 2, ["B"] = 1 },
            remoteClock: new Dictionary<string, ulong> { ["A"] = 1, ["B"] = 2 });

        var resolution = await new CausalConflictResolver().ResolveAsync(conflict);

        // Both versions travel with the Ask — nothing is discarded.
        var ask = Assert.IsType<ConflictResolution.Ask>(resolution);
        Assert.Equal(ConflictAskReason.Concurrent, ask.Reason);
        Assert.Same(conflict, ask.Conflict);
        Assert.Equal(Local, ask.Conflict.LocalVersion);
        Assert.Equal(Remote, ask.Conflict.RemoteVersion);
    }

    [Fact]
    public async Task Concurrent_edits_with_identical_payloads_resolve()
    {
        // Concurrent clocks, but both sides wrote the same bytes: nothing can
        // be discarded, so there is nothing to ask about.
        var same = new byte[] { 9 };
        var conflict = new SyncConflict
        {
            Key = "k",
            LocalVersion = same,
            RemoteVersion = same,
            LocalClock = new Dictionary<string, ulong> { ["A"] = 2, ["B"] = 1 },
            RemoteClock = new Dictionary<string, ulong> { ["A"] = 1, ["B"] = 2 },
        };

        var resolution = await new CausalConflictResolver().ResolveAsync(conflict);

        Assert.Equal(same, ResolvedPayload(resolution));
    }

    [Fact]
    public async Task Equal_clocks_with_identical_payloads_are_not_a_conflict()
    {
        var same = new byte[] { 9 };
        var conflict = new SyncConflict
        {
            Key = "k",
            LocalVersion = same,
            RemoteVersion = same,
            LocalClock = new Dictionary<string, ulong> { ["A"] = 1 },
            RemoteClock = new Dictionary<string, ulong> { ["A"] = 1 },
        };

        var resolution = await new CausalConflictResolver().ResolveAsync(conflict);

        Assert.Equal(same, ResolvedPayload(resolution));
    }

    [Fact]
    public async Task Equal_clocks_with_diverged_payloads_are_surfaced_as_corruption_class()
    {
        var conflict = Conflict(
            localClock: new Dictionary<string, ulong> { ["A"] = 1 },
            remoteClock: new Dictionary<string, ulong> { ["A"] = 1 });

        var resolution = await new CausalConflictResolver().ResolveAsync(conflict);

        var ask = Assert.IsType<ConflictResolution.Ask>(resolution);
        Assert.Equal(ConflictAskReason.EqualClocksDiverged, ask.Reason);
        Assert.Same(conflict, ask.Conflict);
    }

    [Fact]
    public void AddHarborlineLocalFirst_defaults_to_the_causal_resolver()
    {
        using var provider = new ServiceCollection()
            .AddHarborlineLocalFirst()
            .BuildServiceProvider();

        Assert.IsType<CausalConflictResolver>(provider.GetRequiredService<ISyncConflictResolver>());
    }
}
