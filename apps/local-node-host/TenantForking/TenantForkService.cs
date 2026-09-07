using System.Security.Cryptography;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Restore;

namespace Harborline.Api.LocalNodeHost.TenantForking;

/// <summary>The atomic permission required to extract tenant state into a fork.</summary>
public static class TenantForkPermissions
{
    /// <summary>Create an isolated fork from tenant state.</summary>
    public const string Create = "tenant-fork:create";
}

/// <summary>The operational reason for creating an isolated tenant fork.</summary>
public enum TenantForkPurpose
{
    /// <summary>A short-lived, production-fidelity troubleshooting copy.</summary>
    Troubleshooting,

    /// <summary>A PII-treated classroom or training copy.</summary>
    Training,
}

/// <summary>The PII transformation applied before training data leaves its source roster.</summary>
public sealed record TrainingPiiTreatment(string TreatmentId);

/// <summary>The registry-resolved obligations carried by a tenant snapshot.</summary>
public sealed record TenantForkObligation(
    string RetentionClass,
    DateTimeOffset MinimumRetainUntil,
    bool IsUnderLegalHold);

/// <summary>Resolves fork obligations from the tenant's retention and legal-hold authorities.</summary>
public interface ITenantForkObligationAuthority
{
    /// <summary>Resolves the live obligations governing the requested document set.</summary>
    ValueTask<TenantForkObligation> ResolveAsync(
        TenantId tenant,
        IReadOnlyList<string> documentIds,
        DateTimeOffset capturedAt,
        CancellationToken ct = default);
}

/// <summary>
/// Resolves fork obligations from the same retention and legal-hold registries that project ticket-032 envelopes.
/// </summary>
public sealed class RegistryTenantForkObligationAuthority : ITenantForkObligationAuthority
{
    private readonly IRetentionPolicyResolver _retention;
    private readonly ILegalHoldRegistry _holds;

    /// <summary>Creates the single fork projection over the authoritative registries.</summary>
    public RegistryTenantForkObligationAuthority(
        IRetentionPolicyResolver retention,
        ILegalHoldRegistry holds)
    {
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
    }

    /// <inheritdoc />
    public async ValueTask<TenantForkObligation> ResolveAsync(
        TenantId tenant,
        IReadOnlyList<string> documentIds,
        DateTimeOffset capturedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var eventClass = AuditEventClass.Configuration;
        var retention = await _retention.ResolveAsync(tenant, eventClass, capturedAt, ct)
            .ConfigureAwait(false);
        var held = await _holds.IsHeldAsync(tenant, HeldRef.ForClass(eventClass.ToString()), ct)
            .ConfigureAwait(false);
        foreach (var documentId in documentIds)
        {
            held |= await _holds.IsHeldAsync(
                tenant,
                HeldRef.ForRecord("crdt-document", documentId),
                ct).ConfigureAwait(false);
        }

        return new TenantForkObligation(
            eventClass.ToString(),
            retention.MinimumHoldUntil,
            held);
    }
}

/// <summary>Atomically activates a fork roster and retires the tenant's prior fork identities.</summary>
public interface ITenantForkRosterStore
{
    /// <summary>Replaces the active fork roster and returns the node identities that were retired.</summary>
    ValueTask<IReadOnlyList<string>> ReplaceAsync(
        TenantId tenant,
        string rosterId,
        IReadOnlyList<string> activeNodeIds,
        CancellationToken ct = default);
}

/// <summary>In-memory fork-roster lifecycle store for a single process.</summary>
public sealed class InMemoryTenantForkRosterStore : ITenantForkRosterStore
{
    private readonly object _lock = new();
    private readonly Dictionary<TenantId, IReadOnlyList<string>> _active = [];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ReplaceAsync(
        TenantId tenant,
        string rosterId,
        IReadOnlyList<string> activeNodeIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rosterId);
        ArgumentNullException.ThrowIfNull(activeNodeIds);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var retired = _active.GetValueOrDefault(tenant) ?? [];
            _active[tenant] = activeNodeIds.ToArray();
            return ValueTask.FromResult(retired);
        }
    }
}

/// <summary>The snapshot transfer boundary between the source roster and a disconnected fork.</summary>
public interface ITenantForkSnapshotTransfer
{
    /// <summary>Transfers snapshot bytes without establishing a continuing sync edge.</summary>
    ReadOnlyMemory<byte> Transfer(ReadOnlyMemory<byte> snapshot);
}

/// <summary>An in-process one-shot snapshot transfer.</summary>
public sealed class InMemoryTenantForkSnapshotTransfer : ITenantForkSnapshotTransfer
{
    /// <summary>Shared stateless transfer instance.</summary>
    public static InMemoryTenantForkSnapshotTransfer Instance { get; } = new();

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Transfer(ReadOnlyMemory<byte> snapshot) => snapshot.ToArray();
}

/// <summary>Raised when an access grant does not authorize a requested tenant fork.</summary>
public sealed class TenantForkAuthorizationException : Exception
{
    /// <summary>Creates an authorization refusal with a stable audit reason.</summary>
    public TenantForkAuthorizationException(string errorCode)
        : base($"Tenant fork authorization was refused: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    /// <summary>The stable refusal code written to the audit log.</summary>
    public string ErrorCode { get; }
}

/// <summary>Raised when a fork purpose's mandatory data-handling rules are not met.</summary>
public sealed class TenantForkDataHandlingException : Exception
{
    /// <summary>Creates a data-handling refusal with a stable audit reason.</summary>
    public TenantForkDataHandlingException(string errorCode)
        : base($"Tenant fork data handling was refused: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    /// <summary>The stable refusal code written to the audit log.</summary>
    public string ErrorCode { get; }
}

/// <summary>The complete input to one tenant snapshot-and-fork operation.</summary>
public sealed record TenantForkRequest(
    TenantId Tenant,
    ActorId Actor,
    AccessGrant Grant,
    TenantForkPurpose Purpose,
    TrainingPiiTreatment? TrainingPiiTreatment,
    IReadOnlyList<ICrdtDocument> SourceDocuments,
    DateTimeOffset ForkedAt);

/// <summary>A content-verified tenant snapshot instantiated under an isolated roster.</summary>
public sealed record TenantForkResult(
    string RosterId,
    NodeIdentity NodeIdentity,
    IReadOnlyList<ICrdtDocument> Documents,
    IReadOnlyDictionary<string, string> SourceContentHashes,
    TenantForkObligation Obligation,
    IPeerTrustPolicy TrustPolicy,
    IReadOnlyList<string> RetiredNodeIds,
    ulong InitialSequenceNumber,
    TenantForkPurpose Purpose,
    TrainingPiiTreatment? TrainingPiiTreatment);

/// <summary>Creates isolated tenant forks from content-verified CRDT snapshots.</summary>
public sealed class TenantForkService
{
    private readonly ICrdtEngine _engine;
    private readonly INodeIdentityFactory _identities;
    private readonly ITenantForkObligationAuthority _obligations;
    private readonly IAuditLog _audit;
    private readonly ITenantForkRosterStore _rosters;
    private readonly IOutboundSequenceAllocator _sequences;
    private readonly ITenantForkSnapshotTransfer _transfer;
    private readonly TimeProvider _time;
    private readonly IAuthorizationClosureReader _authorization;

    /// <summary>Creates the service over the CRDT, identity, policy, and audit authorities.</summary>
    public TenantForkService(
        ICrdtEngine engine,
        INodeIdentityFactory identities,
        ITenantForkObligationAuthority obligations,
        IAuditLog audit,
        ITenantForkRosterStore rosters,
        IOutboundSequenceAllocator sequences,
        ITenantForkSnapshotTransfer transfer,
        TimeProvider time,
        IAuthorizationClosureReader authorization)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _obligations = obligations ?? throw new ArgumentNullException(nameof(obligations));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    /// <summary>Snapshots tenant documents and instantiates them under new roster and document identities.</summary>
    public async ValueTask<TenantForkResult> ForkAsync(
        TenantForkRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Grant);
        ArgumentNullException.ThrowIfNull(request.SourceDocuments);

        await AuthorizeAsync(request, ct).ConfigureAwait(false);
        if (request.Purpose == TenantForkPurpose.Training
            && string.IsNullOrWhiteSpace(request.TrainingPiiTreatment?.TreatmentId))
        {
            await RefuseDataHandlingAsync(
                request,
                "tenant_fork.training_pii_treatment_required",
                ct).ConfigureAwait(false);
        }

        var obligation = await _obligations.ResolveAsync(
            request.Tenant,
            request.SourceDocuments.Select(static document => document.DocumentId).ToArray(),
            request.ForkedAt,
            ct).ConfigureAwait(false);
        if (request.Purpose == TenantForkPurpose.Training && obligation.IsUnderLegalHold)
        {
            await RefuseDataHandlingAsync(
                request,
                "tenant_fork.training_legal_hold",
                ct).ConfigureAwait(false);
        }

        var transferred = new List<(string SourceId, ReadOnlyMemory<byte> Snapshot)>(
            request.SourceDocuments.Count);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in request.SourceDocuments)
        {
            var snapshot = source.ToSnapshot();
            var expectedHash = SHA256.HashData(snapshot.Span);
            var transferredSnapshot = _transfer.Transfer(snapshot);
            var actualHash = SHA256.HashData(transferredSnapshot.Span);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new ContentHashMismatchException(source.DocumentId);
            }

            var hash = Convert.ToHexStringLower(expectedHash);
            transferred.Add((source.DocumentId, transferredSnapshot));
            hashes.Add(source.DocumentId, hash);
        }

        var rosterId = Guid.NewGuid().ToString("N");
        var identity = _identities.CreateFresh();
        var initialSequence = await _sequences.ReserveNextAsync(identity.NodeId, ct).ConfigureAwait(false);
        var retiredNodeIds = await _rosters.ReplaceAsync(
            request.Tenant,
            rosterId,
            [identity.NodeId],
            ct).ConfigureAwait(false);
        var documents = new List<ICrdtDocument>(transferred.Count);
        foreach (var item in transferred)
        {
            var fork = _engine.CreateDocument($"{rosterId}:{item.SourceId}:{Guid.NewGuid():N}");
            fork.ApplySnapshot(item.Snapshot);
            documents.Add(fork);
        }

        var result = new TenantForkResult(
            rosterId,
            identity,
            documents,
            hashes,
            obligation,
            new MemberSetTrustPolicy([identity.PublicKey]),
            retiredNodeIds,
            initialSequence,
            request.Purpose,
            request.TrainingPiiTreatment);
        await AppendAuditAsync(request, Op.Mint, "tenant_fork.created", rosterId, ct).ConfigureAwait(false);
        return result;
    }

    private async ValueTask AuthorizeAsync(TenantForkRequest request, CancellationToken ct)
    {
        string? refusal = null;
        if (request.Grant.TenantId != request.Tenant)
        {
            refusal = "tenant_fork.cross_tenant_grant";
        }
        else if (request.Grant.Subject != request.Actor)
        {
            refusal = "tenant_fork.wrong_principal";
        }
        else if (!request.Grant.IsActiveAt(_time.GetUtcNow()))
        {
            refusal = "tenant_fork.inactive_grant";
        }
        else if (request.Grant.Residency != GrantResidency.Cache)
        {
            refusal = "tenant_fork.online_only_grant";
        }
        else
        {
            var held = await _authorization.UserPermissionsAsync(
                request.Tenant, request.Actor, _time.GetUtcNow(), ct).ConfigureAwait(false);
            if (request.SourceDocuments.Any(document => !held.Covers(new PermissionAtom(
                AuthorizationOperation.Parse(TenantForkPermissions.Create),
                ScopeExpression.Parse($"/records/{document.DocumentId}")))))
                refusal = "tenant_fork.permission_denied";
        }

        if (refusal is null)
        {
            return;
        }

        await AppendAuditAsync(request, Op.Reject, refusal, rosterId: null, ct).ConfigureAwait(false);
        throw new TenantForkAuthorizationException(refusal);
    }

    private async ValueTask RefuseDataHandlingAsync(
        TenantForkRequest request,
        string errorCode,
        CancellationToken ct)
    {
        await AppendAuditAsync(request, Op.Reject, errorCode, rosterId: null, ct).ConfigureAwait(false);
        throw new TenantForkDataHandlingException(errorCode);
    }

    private async ValueTask AppendAuditAsync(
        TenantForkRequest request,
        Op op,
        string reason,
        string? rosterId,
        CancellationToken ct)
    {
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            reason,
            purpose = request.Purpose.ToString(),
            grantId = request.Grant.GrantId.ToString(),
            rosterId,
            documentIds = request.SourceDocuments.Select(static document => document.DocumentId).ToArray(),
        }));
        await _audit.AppendAsync(new AuditAppend(
            new EntityId("tenant-fork", request.Tenant.Value, request.Grant.GrantId.ToString()),
            VersionId: null,
            op,
            request.Actor,
            request.Tenant,
            _time.GetUtcNow(),
            payload), ct).ConfigureAwait(false);
    }
}
