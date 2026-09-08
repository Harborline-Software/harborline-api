using System.Security.Cryptography;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;

using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Restore;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.BackupRestore;

/// <summary>The operator-supplied coordinates for replacing a node from canonical holders.</summary>
public sealed record NodeRehostRequest(string TenantId, string ReplacedNodeId,
    ActorId Caller, RosterSignedRehostGrant? Grant);

/// <summary>Operator-selected identity and ceremony/holder connections for one manual restore.</summary>
public sealed record NodeRehostSession(NodeIdentity ReplacementIdentity, ITrusteeKeyRecovery KeyRecovery,
    ICanonicalRehostSource CanonicalSource, IHomeEpochPromotionAuthority PromotionAuthority);

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
    /// <summary>Verifies and burns through the gate before any restore writes or holder reads.</summary>
    ValueTask<AuthorizationDecision> RedeemAsync(RosterSignedRehostGrant? grant, string tenantId,
        string replacedNodeId, NodeIdentity replacement, IReadOnlyList<string> requiredActs, ActorId caller,
        CancellationToken ct = default);

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
    PendingHomeEpochAssertion HomeAssertion,
    AuthorizationDecision Decision)
{
    /// <summary>The durable home epoch established for the replacement node.</summary>
    public long HomeEpochNumber => HomeAssertion.AssertedEpoch;
}

/// <summary>
/// Implements manual re-host from the continuously-synced canonical store; it does not create snapshots.
/// </summary>
public sealed class NodeRehostService
{
    private readonly IRootSeedRestorer _rootSeedRestorer;
    private readonly IRosterRehostGrantProvider _grantProvider;
    private readonly IHomeEpochStore _epochs;

    /// <summary>Creates the manual re-host path over the existing recovery, sync, and fencing authorities.</summary>
    public NodeRehostService(
        IRootSeedRestorer rootSeedRestorer,
        IRosterRehostGrantProvider grantProvider,
        IHomeEpochStore epochs)
    {
        _rootSeedRestorer = rootSeedRestorer ?? throw new ArgumentNullException(nameof(rootSeedRestorer));
        _grantProvider = grantProvider ?? throw new ArgumentNullException(nameof(grantProvider));
        _epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
    }

    /// <summary>
    /// Redeems the grant, recovers keys, verifies holder content, then lands the home-epoch cutover.
    /// </summary>
    public async ValueTask<NodeRehostResult> RestoreAsync(
        NodeRehostRequest request,
        NodeRehostSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReplacedNodeId);

        ArgumentNullException.ThrowIfNull(session);
        var replacementIdentity = session.ReplacementIdentity;
        if (string.Equals(replacementIdentity.NodeId, request.ReplacedNodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A re-host must use a fresh node identity.");
        }

        var decision = await _grantProvider.RedeemAsync(request.Grant, request.TenantId,
            request.ReplacedNodeId, replacementIdentity,
            [SignedRosterRehostGrantProvider.ReadCanonical, SignedRosterRehostGrantProvider.PromoteHome],
            request.Caller, ct).ConfigureAwait(false);

        var recovered = await session.KeyRecovery.RecoverAsync(replacementIdentity.NodeId, ct)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(recovered);
        await _rootSeedRestorer.RestoreRootSeedAsync(recovered.RootSeed, ct).ConfigureAwait(false);

        var documents = await session.CanonicalSource.ReConvergeFromHoldersAsync(request.Grant!, ct)
            .ConfigureAwait(false);
        VerifyContentHashes(documents);

        var promotion = await session.PromotionAuthority.AuthorizeRecoveryFailoverAsync(
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
                promotion.HomeDeviceId),
            decision);
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
