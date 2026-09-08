namespace Harborline.Api.Foundation.LocalFirst;

/// <summary>
/// Describes a conflict between a local and remote version of the same key.
/// Callers supply <see cref="CommonAncestor"/> when available for three-way
/// merge strategies, and the per-side vector clocks
/// (<see cref="LocalClock"/> / <see cref="RemoteClock"/>) so the resolver can
/// order the versions causally. ADR 0053: order comes from causality, not
/// timestamps — the timestamps here are display metadata, never a decider.
/// </summary>
public sealed record SyncConflict
{
    /// <summary>Store key being synced.</summary>
    public required string Key { get; init; }

    /// <summary>Local version payload.</summary>
    public required byte[] LocalVersion { get; init; }

    /// <summary>Remote version payload.</summary>
    public required byte[] RemoteVersion { get; init; }

    /// <summary>Optional common ancestor payload for three-way merge.</summary>
    public byte[]? CommonAncestor { get; init; }

    /// <summary>Local modification timestamp, when known. Display metadata only.</summary>
    public DateTimeOffset? LocalModifiedAt { get; init; }

    /// <summary>Remote modification timestamp, when known. Display metadata only.</summary>
    public DateTimeOffset? RemoteModifiedAt { get; init; }

    /// <summary>
    /// Vector clock of the local version: node-id (hex string of the 16-byte
    /// node_id, matching the gossip <c>map&lt;tstr, u64&gt;</c> encoding) to
    /// per-node sequence. Missing entries count as zero. Required: without a
    /// causal basis a conflict cannot be ordered at all (ADR 0053 — timestamps
    /// are not an ordering), so engines that cannot supply clocks cannot
    /// construct a <see cref="SyncConflict"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, ulong> LocalClock { get; init; }

    /// <summary>Vector clock of the remote version. Same shape and requirement as <see cref="LocalClock"/>.</summary>
    public required IReadOnlyDictionary<string, ulong> RemoteClock { get; init; }
}

/// <summary>
/// Why an automatic resolver surfaced a conflict instead of deciding it.
/// </summary>
public enum ConflictAskReason
{
    /// <summary>
    /// The versions are concurrent: neither writer had seen the other's edit,
    /// so any automatic pick would silently discard one of them.
    /// </summary>
    Concurrent = 0,

    /// <summary>
    /// The vector clocks are identical but the payloads diverged. Identical
    /// causal history cannot produce different bytes, so this indicates a
    /// write that failed to tick its clock — a corruption-class state, not
    /// concurrency.
    /// </summary>
    EqualClocksDiverged = 1,
}

/// <summary>
/// Outcome of conflict resolution: either the conflict was decided
/// automatically (<see cref="Resolved"/>), or it is surfaced for a decision
/// (<see cref="Ask"/> — the ADR 0053 "Ask" outcome, the default when causality
/// cannot decide). Surfacing is a normal outcome, not an error.
/// </summary>
public abstract record ConflictResolution
{
    private ConflictResolution()
    {
    }

    /// <summary>The conflict was decided automatically.</summary>
    /// <param name="Payload">The winning (or merged) payload bytes.</param>
    public sealed record Resolved(byte[] Payload) : ConflictResolution;

    /// <summary>
    /// The conflict cannot be decided without discarding an edit nobody saw;
    /// it is surfaced — both versions and their metadata intact — for a person
    /// or a richer module-owned resolver to decide. Nothing is discarded.
    /// </summary>
    /// <param name="Conflict">The undecided conflict, with both versions intact.</param>
    /// <param name="Reason">Why the resolver could not decide.</param>
    /// <param name="Detail">Human-readable description of the undecidable state.</param>
    /// <remarks>
    /// ADR 0053 wants a surfaced conflict to carry "who wrote each" version.
    /// Writer identity is not yet carryable here — <see cref="SyncConflict"/>
    /// has no writer-identity fields — so that gap is deliberately deferred
    /// rather than papered over with a guess.
    /// </remarks>
    public sealed record Ask(SyncConflict Conflict, ConflictAskReason Reason, string Detail) : ConflictResolution;
}

/// <summary>
/// Strategy for merging conflicting versions into a single resolved payload.
/// Modules register their own resolver when they can merge more richly
/// (for example, CRDT-style merges); the default surfaces any conflict it
/// cannot order causally rather than silently picking a side.
/// </summary>
public interface ISyncConflictResolver
{
    /// <summary>
    /// Decides a conflict. Returns <see cref="ConflictResolution.Resolved"/>
    /// when the conflict can be decided without discarding an edit nobody saw,
    /// or <see cref="ConflictResolution.Ask"/> surfacing it for a decision.
    /// </summary>
    ValueTask<ConflictResolution> ResolveAsync(SyncConflict conflict, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default resolver: decides by causal order (ADR 0053). Byte-identical
/// payloads resolve immediately — nothing can be discarded when both sides are
/// the same. Otherwise, when one version's vector clock dominates the other's,
/// the dominated version was already seen by the dominator's writer and the
/// dominator wins. Everything else is surfaced as an
/// <see cref="ConflictResolution.Ask"/> — never decided by wall-clock
/// timestamps, because last-write-wins silently discards the edit nobody saw.
/// </summary>
public sealed class CausalConflictResolver : ISyncConflictResolver
{
    /// <inheritdoc />
    public ValueTask<ConflictResolution> ResolveAsync(SyncConflict conflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        if (conflict.LocalVersion.AsSpan().SequenceEqual(conflict.RemoteVersion))
        {
            // Identical bytes on both sides: nothing can be discarded, whatever
            // the clock relationship is.
            return ValueTask.FromResult<ConflictResolution>(new ConflictResolution.Resolved(conflict.RemoteVersion));
        }

        var (localAhead, remoteAhead) = Compare(conflict.LocalClock, conflict.RemoteClock);

        if (localAhead && !remoteAhead)
        {
            return ValueTask.FromResult<ConflictResolution>(new ConflictResolution.Resolved(conflict.LocalVersion));
        }

        if (remoteAhead && !localAhead)
        {
            return ValueTask.FromResult<ConflictResolution>(new ConflictResolution.Resolved(conflict.RemoteVersion));
        }

        if (localAhead && remoteAhead)
        {
            return ValueTask.FromResult<ConflictResolution>(new ConflictResolution.Ask(
                conflict,
                ConflictAskReason.Concurrent,
                "the versions are concurrent — neither writer saw the other's edit, so an automatic " +
                "pick would silently discard one of them."));
        }

        // Neither side ahead: identical causal history, yet the payloads differ
        // (byte-equality was checked above). A write failed to tick its clock.
        return ValueTask.FromResult<ConflictResolution>(new ConflictResolution.Ask(
            conflict,
            ConflictAskReason.EqualClocksDiverged,
            "the vector clocks are identical but the payloads diverged — identical causal history " +
            "cannot produce different bytes, so a write failed to tick its clock. This is a " +
            "corruption-class state, not concurrency."));
    }

    /// <summary>
    /// Pointwise vector-clock comparison: a node id missing from one side
    /// counts as zero, ids are matched by each dictionary's own key lookup
    /// (ordinal string keys), and the scan exits as soon as both sides are
    /// known to be ahead. First pass walks <paramref name="local"/> against
    /// <paramref name="remote"/>; a second pass only looks for remote ids
    /// absent from local.
    /// </summary>
    private static (bool LocalAhead, bool RemoteAhead) Compare(
        IReadOnlyDictionary<string, ulong> local,
        IReadOnlyDictionary<string, ulong> remote)
    {
        var localAhead = false;
        var remoteAhead = false;

        foreach (var (id, lv) in local)
        {
            var rv = remote.TryGetValue(id, out var raw) ? raw : 0ul;
            if (lv > rv)
            {
                localAhead = true;
            }
            else if (lv < rv)
            {
                remoteAhead = true;
            }

            if (localAhead && remoteAhead)
            {
                return (true, true);
            }
        }

        if (!remoteAhead)
        {
            foreach (var (id, rv) in remote)
            {
                if (rv > 0 && !local.ContainsKey(id))
                {
                    remoteAhead = true;
                    break;
                }
            }
        }

        return (localAhead, remoteAhead);
    }
}
