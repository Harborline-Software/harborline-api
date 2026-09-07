using System.Security.Cryptography;

using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Identity;

namespace Harborline.Api.Kernel.Sync.Restore;

/// <summary>Creates a fresh sync identity for a restored replica.</summary>
public interface INodeIdentityFactory
{
    /// <summary>Creates an identity that has never published under the replaced node id.</summary>
    NodeIdentity CreateFresh();
}

/// <summary>Canonical document bytes and their holder-published SHA-256 digest.</summary>
public sealed record CanonicalReplicaDocument(
    string DocumentId,
    byte[] Snapshot,
    string Sha256);

/// <summary>The roster-authorized state returned by canonical replica holders.</summary>
public sealed record CanonicalReplicaState(
    string RosterSignedGrant,
    IReadOnlyList<CanonicalReplicaDocument> Documents);

/// <summary>Raised when canonical holder bytes do not match their published digest.</summary>
public sealed class ContentHashMismatchException : Exception
{
    /// <summary>Creates a mismatch for the named canonical document.</summary>
    public ContentHashMismatchException(string documentId)
        : base($"Canonical document '{documentId}' failed content-hash verification.")
    {
        DocumentId = documentId;
    }

    /// <summary>The logical document whose canonical bytes failed verification.</summary>
    public string DocumentId { get; }
}

/// <summary>Re-converges a replacement replica from canonical holders.</summary>
public interface IReplicaRehostSource
{
    /// <summary>Returns the canonical state authorized to replace <paramref name="replacedNodeId"/>.</summary>
    ValueTask<CanonicalReplicaState> ReConvergeFromHoldersAsync(
        string replacedNodeId,
        CancellationToken ct = default);
}

/// <summary>The identity, position, and gossip clock presented by a returning replica.</summary>
public sealed record ReturningReplicaState(
    string NodeId,
    ulong PublishedPosition,
    VectorClock VectorClock);

/// <summary>The state admitted after evaluating a returning replica.</summary>
public sealed record ReplicaReturnResult(
    ReplicaReturnDisposition Disposition,
    NodeIdentity? Identity,
    VectorClock VectorClock,
    string? RosterSignedGrant,
    IReadOnlyList<CanonicalReplicaDocument> Documents,
    ulong LastPublishedPosition)
{
    /// <summary>
    /// Opens only the verified holder snapshots as fresh CRDT replicas; restored local bytes are not merged.
    /// </summary>
    public IReadOnlyDictionary<string, ICrdtDocument> OpenRehostedDocuments(ICrdtEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Disposition != ReplicaReturnDisposition.RestoreRequired)
        {
            throw new InvalidOperationException("Only a restore-required result carries re-hosted documents.");
        }

        return Documents.ToDictionary(
            document => document.DocumentId,
            document => engine.OpenDocument(document.DocumentId, document.Snapshot),
            StringComparer.Ordinal);
    }
}

/// <summary>
/// Routes rolled-back replicas through re-host before their old identity, clock, or state is admitted.
/// </summary>
public sealed class ReplicaRestoreCoordinator
{
    private readonly IPublishedPositionStore _positions;
    private readonly ReplicaReturnDetector _detector;
    private readonly IReplicaRehostSource _rehostSource;
    private readonly INodeIdentityFactory _identityFactory;

    /// <summary>Creates the restore coordinator over holder evidence and the ratified re-host ports.</summary>
    public ReplicaRestoreCoordinator(
        IPublishedPositionStore positions,
        IReplicaRehostSource rehostSource,
        INodeIdentityFactory identityFactory)
    {
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _detector = new ReplicaReturnDetector(_positions);
        _rehostSource = rehostSource ?? throw new ArgumentNullException(nameof(rehostSource));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
    }

    /// <summary>
    /// Admits current state unchanged, or replaces a rolled-back replica from canonical holders.
    /// </summary>
    public async ValueTask<ReplicaReturnResult> ReturnAsync(
        ReturningReplicaState returning,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(returning);
        ArgumentException.ThrowIfNullOrWhiteSpace(returning.NodeId);
        ArgumentNullException.ThrowIfNull(returning.VectorClock);

        var decision = await _detector.EvaluateAsync(
            returning.NodeId,
            returning.PublishedPosition,
            ct).ConfigureAwait(false);
        if (decision.Disposition == ReplicaReturnDisposition.AdmitExistingIdentity)
        {
            return new ReplicaReturnResult(
                decision.Disposition,
                Identity: null,
                returning.VectorClock,
                RosterSignedGrant: null,
                Documents: Array.Empty<CanonicalReplicaDocument>(),
                decision.LastPublishedPosition);
        }

        var canonical = await _rehostSource.ReConvergeFromHoldersAsync(returning.NodeId, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(canonical.RosterSignedGrant))
        {
            throw new InvalidDataException("A re-host requires a roster-signed grant.");
        }

        VerifyContentHashes(canonical.Documents);
        var freshIdentity = _identityFactory.CreateFresh();
        if (string.Equals(freshIdentity.NodeId, returning.NodeId, StringComparison.Ordinal))
        {
                throw new InvalidOperationException("A re-host must use a fresh node identity.");
        }

        await _positions.AdvanceAsync(
            freshIdentity.NodeId,
            decision.LastPublishedPosition,
            ct).ConfigureAwait(false);

        return new ReplicaReturnResult(
            decision.Disposition,
            freshIdentity,
            new VectorClock(),
            canonical.RosterSignedGrant,
            canonical.Documents,
            decision.LastPublishedPosition);
    }

    private static void VerifyContentHashes(IReadOnlyList<CanonicalReplicaDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(document.Sha256);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Canonical document '{document.DocumentId}' has an invalid SHA-256 digest.",
                    ex);
            }

            var actual = SHA256.HashData(document.Snapshot);
            if (expected.Length != actual.Length
                || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new ContentHashMismatchException(document.DocumentId);
            }
        }
    }
}
