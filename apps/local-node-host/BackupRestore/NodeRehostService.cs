using System.Security.Cryptography;

using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.BackupRestore;

/// <summary>The operator-supplied coordinates for replacing a node from canonical holders.</summary>
public sealed record NodeRehostRequest(string TenantId, string ReplacedNodeId);

/// <summary>A root seed recovered through the trustee quorum and the trustees that attested it.</summary>
public sealed record RecoveredNodeKeys(byte[] RootSeed, IReadOnlyList<string> AttestingTrusteeNodeIds);

/// <summary>Recovers the replacement node's root seed through the trustee recovery ceremony.</summary>
public interface ITrusteeKeyRecovery
{
    /// <summary>Completes trustee recovery for a fresh replacement node identity.</summary>
    ValueTask<RecoveredNodeKeys> RecoverAsync(
        string replacementNodeId,
        CancellationToken ct = default);
}

/// <summary>A roster-authorized, signed grant admitting one replacement node.</summary>
public sealed record RosterSignedRehostGrant(string SerializedGrant);

/// <summary>Obtains roster authorization for a trustee-attested replacement identity.</summary>
public interface IRosterRehostGrantProvider
{
    /// <summary>Obtains the signed grant that authorizes the replacement for the tenant.</summary>
    ValueTask<RosterSignedRehostGrant> ObtainAsync(
        string tenantId,
        string replacedNodeId,
        NodeIdentity replacementIdentity,
        IReadOnlyList<string> attestingTrusteeNodeIds,
        CancellationToken ct = default);
}

/// <summary>Reads canonical synced documents from holders under a roster-signed re-host grant.</summary>
public interface ICanonicalRehostSource
{
    /// <summary>Re-converges all selectively-synced canonical documents authorized by the grant.</summary>
    ValueTask<IReadOnlyList<CanonicalReplicaDocument>> ReConvergeFromHoldersAsync(
        RosterSignedRehostGrant grant,
        CancellationToken ct = default);
}

/// <summary>Obtains the roster-signed, multi-actor home-epoch promotion for a recovery failover.</summary>
public interface IHomeEpochPromotionAuthority
{
    /// <summary>Authorizes the replacement node as the tenant's new home.</summary>
    ValueTask<HomeEpochRecord> AuthorizeRecoveryFailoverAsync(
        string tenantId,
        string replacementNodeId,
        CancellationToken ct = default);
}

/// <summary>The verified canonical state and fencing epoch produced by a completed re-host.</summary>
public sealed record NodeRehostResult(
    NodeIdentity Identity,
    IReadOnlyList<CanonicalReplicaDocument> Documents,
    PendingHomeEpochAssertion HomeAssertion)
{
    /// <summary>The durable home epoch established for the replacement node.</summary>
    public long HomeEpochNumber => HomeAssertion.AssertedEpoch;
}

/// <summary>
/// Implements manual re-host from the continuously-synced canonical store; it does not create snapshots.
/// </summary>
public sealed class NodeRehostService
{
    private readonly ITrusteeKeyRecovery _keyRecovery;
    private readonly IRootSeedRestorer _rootSeedRestorer;
    private readonly INodeIdentityFactory _identityFactory;
    private readonly IRosterRehostGrantProvider _grantProvider;
    private readonly ICanonicalRehostSource _canonicalSource;
    private readonly IHomeEpochPromotionAuthority _promotionAuthority;
    private readonly IHomeEpochStore _epochs;

    /// <summary>Creates the manual re-host path over the existing recovery, sync, and fencing authorities.</summary>
    public NodeRehostService(
        ITrusteeKeyRecovery keyRecovery,
        IRootSeedRestorer rootSeedRestorer,
        INodeIdentityFactory identityFactory,
        IRosterRehostGrantProvider grantProvider,
        ICanonicalRehostSource canonicalSource,
        IHomeEpochPromotionAuthority promotionAuthority,
        IHomeEpochStore epochs)
    {
        _keyRecovery = keyRecovery ?? throw new ArgumentNullException(nameof(keyRecovery));
        _rootSeedRestorer = rootSeedRestorer ?? throw new ArgumentNullException(nameof(rootSeedRestorer));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _grantProvider = grantProvider ?? throw new ArgumentNullException(nameof(grantProvider));
        _canonicalSource = canonicalSource ?? throw new ArgumentNullException(nameof(canonicalSource));
        _promotionAuthority = promotionAuthority ?? throw new ArgumentNullException(nameof(promotionAuthority));
        _epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
    }

    /// <summary>
    /// Recovers keys, obtains a roster grant, verifies holder content, then lands the home-epoch cutover.
    /// </summary>
    public async ValueTask<NodeRehostResult> RestoreAsync(
        NodeRehostRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReplacedNodeId);

        var replacementIdentity = _identityFactory.CreateFresh();
        if (string.Equals(replacementIdentity.NodeId, request.ReplacedNodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A re-host must use a fresh node identity.");
        }

        var recovered = await _keyRecovery.RecoverAsync(replacementIdentity.NodeId, ct)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(recovered);
        await _rootSeedRestorer.RestoreRootSeedAsync(recovered.RootSeed, ct).ConfigureAwait(false);

        var grant = await _grantProvider.ObtainAsync(
            request.TenantId,
            request.ReplacedNodeId,
            replacementIdentity,
            recovered.AttestingTrusteeNodeIds,
            ct).ConfigureAwait(false);
        if (grant is null || string.IsNullOrWhiteSpace(grant.SerializedGrant))
        {
            throw new InvalidDataException("A re-host requires a roster-signed grant.");
        }

        var documents = await _canonicalSource.ReConvergeFromHoldersAsync(grant, ct)
            .ConfigureAwait(false);
        VerifyContentHashes(documents);

        var promotion = await _promotionAuthority.AuthorizeRecoveryFailoverAsync(
            request.TenantId,
            replacementIdentity.NodeId,
            ct).ConfigureAwait(false);
        if (promotion is null
            || !string.Equals(promotion.TenantId, request.TenantId, StringComparison.Ordinal)
            || !string.Equals(promotion.HomeDeviceId, replacementIdentity.NodeId, StringComparison.Ordinal)
            || promotion.PromotionKind != HomePromotionKind.RecoveryFailover)
        {
            throw new InvalidDataException(
                "The authorized home-epoch promotion does not designate this replacement node.");
        }

        await _epochs.AdvanceAsync(promotion, ct).ConfigureAwait(false);

        return new NodeRehostResult(
            replacementIdentity,
            documents,
            new PendingHomeEpochAssertion(
                promotion.TenantId,
                promotion.EpochNumber,
                promotion.HomeDeviceId));
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
