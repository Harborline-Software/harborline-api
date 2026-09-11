using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal enum TenantMembershipStatus
{
    Active,
    Revoked,
}

internal enum TenantMembershipIntentState
{
    Prepared,
    Finalized,
    Aborted,
}

internal enum TenantSessionSelectionIntentState
{
    Prepared,
    Finalized,
    Aborted,
}

internal enum TenantSessionRevocationIntentState
{
    Prepared,
    Finalized,
    Aborted,
}

internal sealed record TenantMembershipMutation(
    string TenantId,
    string CanonicalPrincipalId,
    string GrantId,
    long ExpectedGrantOwnerVersion,
    long AuthorizationEpoch,
    long ExpectedMembershipOwnerVersion,
    TenantMembershipStatus TargetStatus,
    IReadOnlyList<string>? RequestedPermissions = null,
    long? ResultingGrantOwnerVersion = null,
    long? ResultingAuthorizationEpoch = null);

internal sealed record TenantMembershipSnapshot(
    string MembershipId,
    string AccountId,
    string TenantId,
    string CanonicalPrincipalId,
    string GrantId,
    long GrantOwnerVersion,
    long AuthorizationEpoch,
    TenantMembershipStatus Status,
    long OwnerVersion);

internal sealed record TenantMembershipFinalizationReceipt(
    string TenantId,
    long DocumentOwnerVersion,
    string MembershipId,
    long MembershipOwnerVersion,
    string MembershipDigest,
    long AuditSequence,
    string AuditHeadHash,
    string IntentDigest,
    string HomeDecisionDigest);

internal sealed record TenantSessionSelectionReceipt(
    string TenantId,
    long DocumentOwnerVersion,
    string MembershipId,
    long AuditSequence,
    string AuditHeadHash,
    string IntentDigest,
    string HomeDecisionDigest);

internal sealed record TenantSessionRevocationReceipt(
    string TenantId,
    long DocumentOwnerVersion,
    string MembershipId,
    string SessionCorrelationId,
    long AuditSequence,
    string AuditHeadHash,
    string IntentDigest,
    string HomeDecisionDigest);

internal sealed record InstallationIdentityHomeDecisionReceipt(
    string CorrelationId,
    string CommandFingerprint,
    InstallationIdentityCoordinatorState State,
    long CoordinatorOwnerVersion,
    string TenantId,
    string DecisionDigest);

internal interface IInstallationIdentityHomeDecisionAuthority
{
    Task<InstallationIdentityHomeDecisionReceipt> RequireFinalizationAsync(
        string correlationId,
        string commandFingerprint,
        string tenantId,
        CancellationToken cancellationToken);

    Task<InstallationIdentityHomeDecisionReceipt> RequireAbortAsync(
        string correlationId,
        string commandFingerprint,
        string tenantId,
        CancellationToken cancellationToken);
}

internal interface ITenantMembershipAuthorityStore
{
    string TenantId { get; }

    Task PrepareAsync(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string actorAccountId,
        string authorityEvidenceDigest,
        TenantMembershipMutation mutation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task AbortAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task<TenantMembershipSnapshot?> GetMembershipAsync(
        string accountId,
        CancellationToken cancellationToken);

    Task<TenantMembershipIntentState?> GetIntentStateAsync(
        string correlationId,
        CancellationToken cancellationToken);

    Task<bool> IsAdmissionBlockedAsync(string accountId, CancellationToken cancellationToken);

    Task PrepareSessionSelectionAsync(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string membershipId,
        string payloadDigest,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task AbortSessionSelectionAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task PrepareSessionRevocationAsync(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string membershipId,
        string sessionCorrelationId,
        string payloadDigest,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);

    Task AbortSessionRevocationAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Optional lookup used by grant mutations to locate the matching tenant membership.</summary>
internal interface ITenantMembershipGrantLookup
{
    Task<TenantMembershipSnapshot?> FindMembershipByGrantAsync(
        string grantId,
        CancellationToken cancellationToken);
}

internal sealed record TenantIdentityAuthorityPartition(
    string TenantId,
    ITenantMembershipAuthorityStore Memberships,
    ILeaseCoordinator Leases);

internal interface ITenantIdentityAuthorityPartitionResolver
{
    Task<TenantIdentityAuthorityPartition> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Required live-admission seam. Implementations resolve the canonical tenant Party/trust mapping
/// and current tenant grant, then verify the exact grant owner version and authorization epoch.
/// </summary>
internal interface ITenantMembershipAuthorityAdmission
{
    Task ValidateMutationAsync(
        string actorAccountId,
        string authorityEvidenceDigest,
        string accountId,
        TenantMembershipMutation mutation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Admits an EXISTING membership and returns the authorization epoch in force for it now.
    /// Ticket 362 slice 3: the stored epoch is a monotone floor, not an equality fence. An
    /// administrator's narrowing/revocation advances the member's per-principal epoch and no writer
    /// re-pins the membership document (the coordinator's re-pin arm is retired — see
    /// <c>InstallationIdentityCoordinatorService.ApplyGrantMutationAsync</c>), so an equality fence
    /// here locked a narrowed member out of their own install permanently. The caller re-pins the
    /// snapshot it mints a session from to the value returned here, AFTER this seam re-read the live
    /// grant row; a LOWER live epoch than the pin is still refused, so the fence can only move
    /// forward and a stale pin can never be resurrected.
    /// </summary>
    Task<long> ValidateExistingAsync(
        string accountId,
        TenantMembershipSnapshot membership,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves an explicitly named tenant partition without reading or mutating desktop active-team
/// state. Recovery may materialize the named tenant context after the durable home row is verified.
/// </summary>
internal sealed class TeamContextTenantIdentityAuthorityPartitionResolver(
    ITeamContextFactory teamContexts,
    IInstallationIdentityHomeDecisionAuthority homeDecisions,
    TimeProvider timeProvider) : ITenantIdentityAuthorityPartitionResolver
{
    private readonly ITeamContextFactory _teamContexts =
        teamContexts ?? throw new ArgumentNullException(nameof(teamContexts));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IInstallationIdentityHomeDecisionAuthority _homeDecisions =
        homeDecisions ?? throw new ArgumentNullException(nameof(homeDecisions));

    public Task<TenantIdentityAuthorityPartition> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var teamId = TeamId.Parse(tenantId);
        return ResolveMaterializedAsync(teamId, tenantId, cancellationToken);
    }

    private async Task<TenantIdentityAuthorityPartition> ResolveMaterializedAsync(
        TeamId teamId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var context = await _teamContexts.GetOrCreateAsync(
                teamId,
                $"Identity recovery {tenantId}",
                cancellationToken)
            .ConfigureAwait(false);
        var encryptedStore = context.Services.GetRequiredService<IEncryptedStore>();
        var leases = context.Services.GetRequiredService<ILeaseCoordinator>();
        ITenantMembershipAuthorityStore memberships =
            new EncryptedTenantMembershipAuthorityStore(
                encryptedStore,
                tenantId,
                _homeDecisions,
                _timeProvider);
        return new TenantIdentityAuthorityPartition(tenantId, memberships, leases);
    }
}

/// <summary>
/// Tenant-owned membership, pending-intent, and identity-audit authority persisted as one CAS
/// document. One successful compare-exchange changes all three or none of them. This document is
/// the canonical authority evidence for identity-membership transitions; ADR 0126's general audit
/// table receives a later idempotent projection and is not the transition authority.
/// </summary>
internal sealed class EncryptedTenantMembershipAuthorityStore : ITenantMembershipAuthorityStore, ITenantMembershipGrantLookup
{
    private const string DocumentKey = "identity/tenant-membership-authority/v3";
    private const int SchemaVersion = 3;
    private const int CasRetryLimit = 16;
    private const int SerializedDocumentByteCeiling = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEncryptedStore _store;
    private readonly IInstallationIdentityHomeDecisionAuthority _homeDecisions;
    private readonly TimeProvider _timeProvider;

    internal EncryptedTenantMembershipAuthorityStore(
        IEncryptedStore store,
        string tenantId,
        IInstallationIdentityHomeDecisionAuthority homeDecisions,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        TenantId = tenantId;
        _homeDecisions = homeDecisions ?? throw new ArgumentNullException(nameof(homeDecisions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string TenantId { get; }

    public Task PrepareAsync(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string actorAccountId,
        string authorityEvidenceDigest,
        TenantMembershipMutation mutation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(mutation.TenantId, TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.tenant_binding_mismatch: mutation does not belong to this tenant store.");
        }

        return MutateAsync(
            document => Prepare(
                document,
                correlationId,
                commandFingerprint,
                accountId,
                actorAccountId,
                authorityEvidenceDigest,
                mutation,
                occurredAtUtc),
            cancellationToken);
    }

    public async Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var homeDecision = await _homeDecisions.RequireFinalizationAsync(
                correlationId,
                commandFingerprint,
                TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        await MutateAsync(
            document => Finalize(
                document,
                correlationId,
                commandFingerprint,
                homeDecision,
                occurredAtUtc),
            cancellationToken).ConfigureAwait(false);
        return await GetFinalizationReceiptAsync(correlationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task AbortAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var homeDecision = await _homeDecisions.RequireAbortAsync(
                correlationId,
                commandFingerprint,
                TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        await MutateAsync(
            document => Abort(
                document,
                correlationId,
                commandFingerprint,
                homeDecision,
                occurredAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TenantMembershipSnapshot?> GetMembershipAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var membership = document.Memberships.SingleOrDefault(candidate => candidate.AccountId == accountId);
        return membership is null ? null : Project(membership);
    }

    public async Task<TenantMembershipSnapshot?> FindMembershipByGrantAsync(
        string grantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var membership = document.Memberships.SingleOrDefault(candidate => candidate.GrantId == grantId);
        return membership is null ? null : Project(membership);
    }

    public async Task<TenantMembershipIntentState?> GetIntentStateAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.Intents.SingleOrDefault(candidate => candidate.CorrelationId == correlationId)?.State;
    }

    public async Task<bool> IsAdmissionBlockedAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.Intents.Any(candidate =>
            candidate.AccountId == accountId && candidate.State == TenantMembershipIntentState.Prepared);
    }

    public Task PrepareSessionSelectionAsync(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string membershipId,
        string payloadDigest,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        MutateAsync(
            document => PrepareSessionSelection(
                document,
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                payloadDigest,
                occurredAtUtc),
            cancellationToken);

    public async Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var homeDecision = await _homeDecisions.RequireFinalizationAsync(
                correlationId,
                commandFingerprint,
                TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        await MutateAsync(
            document => FinalizeSessionSelection(
                document,
                correlationId,
                commandFingerprint,
                homeDecision,
                occurredAtUtc),
            cancellationToken).ConfigureAwait(false);
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.SessionSelections!
            .Single(item => item.CorrelationId == correlationId)
            .FinalizationReceipt
            ?? throw new InvalidOperationException(
                "identity.session_selection_receipt_missing: finalized selection has no receipt.");
    }

    public async Task AbortSessionSelectionAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var homeDecision = await _homeDecisions.RequireAbortAsync(
                correlationId,
                commandFingerprint,
                TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        await MutateAsync(
            document => AbortSessionSelection(
                document,
                correlationId,
                commandFingerprint,
                homeDecision,
                occurredAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    public Task PrepareSessionRevocationAsync(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string membershipId,
        string sessionCorrelationId,
        string payloadDigest,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        MutateAsync(
            document => PrepareSessionRevocation(
                document,
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                sessionCorrelationId,
                payloadDigest,
                occurredAtUtc),
            cancellationToken);

    public async Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var homeDecision = await _homeDecisions.RequireFinalizationAsync(
                correlationId,
                commandFingerprint,
                TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        await MutateAsync(
            document => FinalizeSessionRevocation(
                document,
                correlationId,
                commandFingerprint,
                homeDecision,
                occurredAtUtc),
            cancellationToken).ConfigureAwait(false);
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.SessionRevocations!
            .Single(item => item.CorrelationId == correlationId)
            .FinalizationReceipt
            ?? throw new InvalidOperationException(
                "identity.session_revocation_receipt_missing: finalized revocation has no receipt.");
    }

    public async Task AbortSessionRevocationAsync(
        string correlationId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var homeDecision = await _homeDecisions.RequireAbortAsync(
                correlationId,
                commandFingerprint,
                TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        await MutateAsync(
            document => AbortSessionRevocation(
                document,
                correlationId,
                commandFingerprint,
                homeDecision,
                occurredAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TenantMembershipFinalizationReceipt> GetFinalizationReceiptAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        var (_, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var intent = document.Intents.Single(candidate => candidate.CorrelationId == correlationId);
        if (intent.State != TenantMembershipIntentState.Finalized)
        {
            throw new InvalidOperationException(
                "identity.membership_not_finalized: a finalization receipt is not available.");
        }

        return intent.FinalizationReceipt
            ?? throw new InvalidOperationException(
                "identity.membership_receipt_missing: finalized intent has no durable receipt.");
    }

    private async Task MutateAsync(
        Func<TenantAuthorityDocument, bool> mutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CasRetryLimit; attempt++)
        {
            var (observed, document) = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!mutation(document))
            {
                return;
            }

            document.OwnerVersion++;
            document.UpdatedAtUtc = _timeProvider.GetUtcNow();
            SealFinalizationReceipts(document);
            SealSessionSelectionReceipts(document);
            SealSessionRevocationReceipts(document);
            var replacement = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (replacement.Length > SerializedDocumentByteCeiling)
            {
                throw new InvalidOperationException(
                    "identity.tenant_authority_size_exceeded: authority document exceeds 4 MiB.");
            }
            ReadOnlyMemory<byte>? expected = observed is null
                ? (ReadOnlyMemory<byte>?)null
                : new ReadOnlyMemory<byte>(observed);
            if (await _store.CompareExchangeAsync(
                    DocumentKey,
                    expected,
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "identity.tenant_authority_contention: compare-exchange retry budget exhausted.");
    }

    private async Task<(byte[]? Observed, TenantAuthorityDocument Document)> LoadAsync(
        CancellationToken cancellationToken)
    {
        var observed = await _store.GetAsync(DocumentKey, cancellationToken).ConfigureAwait(false);
        if (observed is null)
        {
            var now = _timeProvider.GetUtcNow();
            return (null, new TenantAuthorityDocument
            {
                SchemaVersion = SchemaVersion,
                TenantId = TenantId,
                OwnerVersion = 0,
                Memberships = [],
                Intents = [],
                SessionSelections = [],
                SessionRevocations = [],
                AuditEnvelopes = [],
                AuditHeadSequence = 0,
                AuditHeadHash = InstallationAuditIntegrity.ZeroHash,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        if (observed.Length > SerializedDocumentByteCeiling)
        {
            throw new InvalidOperationException(
                "identity.tenant_authority_size_exceeded: authority document exceeds 4 MiB.");
        }

        var document = JsonSerializer.Deserialize<TenantAuthorityDocument>(observed, JsonOptions)
            ?? throw new InvalidOperationException(
                "identity.tenant_authority_invalid: authority document could not be decoded.");
        if (document.SchemaVersion != SchemaVersion ||
            !string.Equals(document.TenantId, TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.tenant_authority_invalid: schema or tenant binding mismatch.");
        }

        document.SessionSelections ??= [];
        document.SessionRevocations ??= [];

        ValidateIntegrity(document);

        return (observed, document);
    }

    private static bool Prepare(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        string accountId,
        string actorAccountId,
        string authorityEvidenceDigest,
        TenantMembershipMutation mutation,
        DateTimeOffset occurredAtUtc)
    {
        ValidateEnvelopeIdentity(
            correlationId,
            commandFingerprint,
            accountId,
            actorAccountId,
            authorityEvidenceDigest,
            mutation);
        var existingIntent = document.Intents.SingleOrDefault(item => item.CorrelationId == correlationId);
        if (existingIntent is not null)
        {
            RequireSameFingerprint(existingIntent, commandFingerprint);
            if (existingIntent.State == TenantMembershipIntentState.Aborted)
            {
                throw new InvalidOperationException(
                    "identity.membership_intent_aborted: an aborted decision cannot be prepared again.");
            }

            return false;
        }

        if (document.Intents.Any(item =>
                item.AccountId == accountId && item.State == TenantMembershipIntentState.Prepared))
        {
            throw new InvalidOperationException(
                "identity.membership_busy: another membership command is already prepared.");
        }

        var current = document.Memberships.SingleOrDefault(item => item.AccountId == accountId);
        if (current is null && mutation.ExpectedMembershipOwnerVersion != 0)
        {
            throw new InvalidOperationException("identity.membership_version_stale: membership is absent.");
        }
        if (current is not null && current.OwnerVersion != mutation.ExpectedMembershipOwnerVersion)
        {
            throw new InvalidOperationException("identity.membership_version_stale: owner version mismatch.");
        }
        if (current is null && mutation.TargetStatus == TenantMembershipStatus.Revoked)
        {
            throw new InvalidOperationException("identity.membership_missing: membership cannot be revoked.");
        }

        var proposed = new TenantMembershipDocument
        {
            MembershipId = current?.MembershipId ?? RandomHex(32),
            AccountId = accountId,
            TenantId = mutation.TenantId,
            CanonicalPrincipalId = mutation.CanonicalPrincipalId,
            GrantId = mutation.GrantId,
            GrantOwnerVersion = mutation.ResultingGrantOwnerVersion ?? mutation.ExpectedGrantOwnerVersion,
            AuthorizationEpoch = mutation.ResultingAuthorizationEpoch ?? mutation.AuthorizationEpoch,
            Status = mutation.TargetStatus,
            OwnerVersion = (current?.OwnerVersion ?? 0) + 1,
            CreatedAtUtc = current?.CreatedAtUtc ?? occurredAtUtc,
            UpdatedAtUtc = occurredAtUtc,
        };
        var payloadDigest = ComputeMembershipDigest(proposed);
        var intentDigest = InstallationAuditIntegrity.Hash(
            correlationId,
            commandFingerprint,
            accountId,
            actorAccountId,
            authorityEvidenceDigest,
            payloadDigest);
        document.Intents.Add(new TenantMembershipIntentDocument
        {
            CorrelationId = correlationId,
            CommandFingerprint = commandFingerprint,
            AccountId = accountId,
            ActorAccountId = actorAccountId,
            AuthorityEvidenceDigest = authorityEvidenceDigest,
            State = TenantMembershipIntentState.Prepared,
            ProposedMembership = proposed,
            PayloadDigest = payloadDigest,
            IntentDigest = intentDigest,
            PreparedAtUtc = occurredAtUtc,
        });
        return true;
    }

    private static string ComputeMembershipDigest(TenantMembershipDocument proposed) =>
        InstallationAuditIntegrity.Hash(
            proposed.MembershipId,
            proposed.AccountId,
            proposed.TenantId,
            proposed.CanonicalPrincipalId,
            proposed.GrantId,
            proposed.GrantOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            proposed.AuthorizationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            proposed.Status.ToString(),
            proposed.OwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static bool PrepareSessionSelection(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        string accountId,
        string membershipId,
        string payloadDigest,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipId);
        if (payloadDigest.Length != 64)
        {
            throw new ArgumentException("Session-selection payload requires one SHA-256 digest.");
        }

        var sessionSelections = document.SessionSelections ?? throw InvalidAuthorityDocument();
        var existing = sessionSelections
            .SingleOrDefault(item => item.CorrelationId == correlationId);
        if (existing is not null)
        {
            if (!string.Equals(existing.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "identity.session_selection_changed_replay: correlation was reused.");
            }
            if (existing.State == TenantSessionSelectionIntentState.Aborted)
            {
                throw new InvalidOperationException(
                    "identity.session_selection_aborted: an aborted selection cannot resume.");
            }
            return false;
        }

        var membership = document.Memberships.SingleOrDefault(item =>
            item.AccountId == accountId && item.MembershipId == membershipId);
        if (membership is null || membership.Status != TenantMembershipStatus.Active)
        {
            throw new InvalidOperationException(
                "identity.membership_unavailable: selection requires the live tenant membership.");
        }

        sessionSelections.Add(new TenantSessionSelectionIntentDocument
        {
            CorrelationId = correlationId,
            CommandFingerprint = commandFingerprint,
            AccountId = accountId,
            MembershipId = membershipId,
            PayloadDigest = payloadDigest,
            IntentDigest = InstallationAuditIntegrity.Hash(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                payloadDigest),
            State = TenantSessionSelectionIntentState.Prepared,
            PreparedAtUtc = occurredAtUtc,
        });
        return true;
    }

    private static bool FinalizeSessionSelection(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        InstallationIdentityHomeDecisionReceipt homeDecision,
        DateTimeOffset occurredAtUtc)
    {
        var intent = document.SessionSelections!
            .SingleOrDefault(item => item.CorrelationId == correlationId)
            ?? throw new InvalidOperationException(
                "identity.session_selection_intent_missing: prepare is required.");
        if (!string.Equals(intent.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.session_selection_changed_replay: correlation was reused.");
        }
        if (intent.State == TenantSessionSelectionIntentState.Finalized)
        {
            return false;
        }
        if (intent.State == TenantSessionSelectionIntentState.Aborted)
        {
            throw new InvalidOperationException(
                "identity.session_selection_aborted: an aborted selection cannot finalize.");
        }

        var sequence = document.AuditHeadSequence + 1;
        var envelope = new TenantIdentityAuditEnvelopeDocument
        {
            Sequence = sequence,
            CorrelationId = correlationId,
            EventType = "WebTenantSelected",
            AccountId = intent.AccountId,
            ActorAccountId = intent.AccountId,
            AuthorityEvidenceDigest = intent.PayloadDigest,
            MembershipId = intent.MembershipId,
            PreviousHash = document.AuditHeadHash,
            PayloadDigest = intent.PayloadDigest,
            OccurredAtUtc = occurredAtUtc,
            EnvelopeHash = string.Empty,
        };
        envelope.EnvelopeHash = ComputeTenantAuditEnvelopeHash(document.TenantId, envelope);
        document.AuditEnvelopes.Add(envelope);
        document.AuditHeadSequence = sequence;
        document.AuditHeadHash = envelope.EnvelopeHash;
        intent.State = TenantSessionSelectionIntentState.Finalized;
        intent.FinalizationHomeDecisionDigest = homeDecision.DecisionDigest;
        intent.FinalizedAtUtc = occurredAtUtc;
        return true;
    }

    private static bool PrepareSessionRevocation(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        string accountId,
        string membershipId,
        string sessionCorrelationId,
        string payloadDigest,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCorrelationId);
        if (payloadDigest.Length != 64)
        {
            throw new ArgumentException("Session-revocation payload requires one SHA-256 digest.");
        }

        var revocations = document.SessionRevocations ?? throw InvalidAuthorityDocument();
        var existing = revocations.SingleOrDefault(item => item.CorrelationId == correlationId);
        if (existing is not null)
        {
            if (!string.Equals(existing.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "identity.session_revocation_changed_replay: correlation was reused.");
            }
            if (existing.State == TenantSessionRevocationIntentState.Aborted)
            {
                throw new InvalidOperationException(
                    "identity.session_revocation_aborted: an aborted revocation cannot resume.");
            }
            return false;
        }

        var membership = document.Memberships.SingleOrDefault(item =>
            item.AccountId == accountId && item.MembershipId == membershipId);
        if (membership is null || membership.Status != TenantMembershipStatus.Active)
        {
            throw new InvalidOperationException(
                "identity.membership_unavailable: session revocation requires the owning membership.");
        }

        revocations.Add(new TenantSessionRevocationIntentDocument
        {
            CorrelationId = correlationId,
            CommandFingerprint = commandFingerprint,
            AccountId = accountId,
            MembershipId = membershipId,
            SessionCorrelationId = sessionCorrelationId,
            PayloadDigest = payloadDigest,
            IntentDigest = InstallationAuditIntegrity.Hash(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                sessionCorrelationId,
                payloadDigest),
            State = TenantSessionRevocationIntentState.Prepared,
            PreparedAtUtc = occurredAtUtc,
        });
        return true;
    }

    private static bool FinalizeSessionRevocation(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        InstallationIdentityHomeDecisionReceipt homeDecision,
        DateTimeOffset occurredAtUtc)
    {
        var intent = document.SessionRevocations!
            .SingleOrDefault(item => item.CorrelationId == correlationId)
            ?? throw new InvalidOperationException(
                "identity.session_revocation_intent_missing: prepare is required.");
        if (!string.Equals(intent.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.session_revocation_changed_replay: correlation was reused.");
        }
        if (intent.State == TenantSessionRevocationIntentState.Finalized)
        {
            return false;
        }
        if (intent.State == TenantSessionRevocationIntentState.Aborted)
        {
            throw new InvalidOperationException(
                "identity.session_revocation_aborted: an aborted revocation cannot finalize.");
        }

        var sequence = document.AuditHeadSequence + 1;
        var envelope = new TenantIdentityAuditEnvelopeDocument
        {
            Sequence = sequence,
            CorrelationId = correlationId,
            EventType = "WebUserSessionRevoked",
            AccountId = intent.AccountId,
            ActorAccountId = intent.AccountId,
            AuthorityEvidenceDigest = intent.PayloadDigest,
            MembershipId = intent.MembershipId,
            PreviousHash = document.AuditHeadHash,
            PayloadDigest = intent.PayloadDigest,
            OccurredAtUtc = occurredAtUtc,
            EnvelopeHash = string.Empty,
        };
        envelope.EnvelopeHash = ComputeTenantAuditEnvelopeHash(document.TenantId, envelope);
        document.AuditEnvelopes.Add(envelope);
        document.AuditHeadSequence = sequence;
        document.AuditHeadHash = envelope.EnvelopeHash;
        intent.State = TenantSessionRevocationIntentState.Finalized;
        intent.FinalizationHomeDecisionDigest = homeDecision.DecisionDigest;
        intent.FinalizedAtUtc = occurredAtUtc;
        return true;
    }

    private static bool Finalize(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        InstallationIdentityHomeDecisionReceipt homeDecision,
        DateTimeOffset occurredAtUtc)
    {
        var intent = document.Intents.SingleOrDefault(item => item.CorrelationId == correlationId)
            ?? throw new InvalidOperationException("identity.membership_intent_missing: prepare is required.");
        RequireSameFingerprint(intent, commandFingerprint);
        if (intent.State == TenantMembershipIntentState.Finalized)
        {
            return false;
        }
        if (intent.State == TenantMembershipIntentState.Aborted)
        {
            throw new InvalidOperationException(
                "identity.membership_intent_aborted: an aborted decision cannot be finalized.");
        }

        document.Memberships.RemoveAll(item => item.AccountId == intent.AccountId);
        document.Memberships.Add(intent.ProposedMembership);
        var sequence = document.AuditHeadSequence + 1;
        var envelope = new TenantIdentityAuditEnvelopeDocument
        {
            Sequence = sequence,
            CorrelationId = correlationId,
            EventType = "TenantMembershipChanged",
            AccountId = intent.AccountId,
            ActorAccountId = intent.ActorAccountId,
            AuthorityEvidenceDigest = intent.AuthorityEvidenceDigest,
            MembershipId = intent.ProposedMembership.MembershipId,
            PreviousHash = document.AuditHeadHash,
            PayloadDigest = intent.PayloadDigest,
            OccurredAtUtc = occurredAtUtc,
            EnvelopeHash = string.Empty,
        };
        envelope.EnvelopeHash = InstallationAuditIntegrity.Hash(
            document.TenantId,
            envelope.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.CorrelationId,
            envelope.EventType,
            envelope.AccountId,
            envelope.ActorAccountId,
            envelope.AuthorityEvidenceDigest,
            envelope.MembershipId,
            envelope.PreviousHash,
            envelope.PayloadDigest,
            envelope.OccurredAtUtc.ToUnixTimeMilliseconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        document.AuditEnvelopes.Add(envelope);
        document.AuditHeadSequence = sequence;
        document.AuditHeadHash = envelope.EnvelopeHash;
        intent.State = TenantMembershipIntentState.Finalized;
        intent.FinalizationHomeDecisionDigest = homeDecision.DecisionDigest;
        intent.FinalizedAtUtc = occurredAtUtc;
        return true;
    }

    private static void SealFinalizationReceipts(TenantAuthorityDocument document)
    {
        foreach (var intent in document.Intents.Where(candidate =>
                     candidate.State == TenantMembershipIntentState.Finalized &&
                     candidate.FinalizationReceipt is null))
        {
            intent.FinalizationReceipt = new TenantMembershipFinalizationReceipt(
                document.TenantId,
                document.OwnerVersion,
                intent.ProposedMembership.MembershipId,
                intent.ProposedMembership.OwnerVersion,
                intent.PayloadDigest,
                document.AuditHeadSequence,
                document.AuditHeadHash,
                intent.IntentDigest,
                intent.FinalizationHomeDecisionDigest
                    ?? throw InvalidAuthorityDocument());
        }
    }

    private static void SealSessionSelectionReceipts(TenantAuthorityDocument document)
    {
        foreach (var intent in document.SessionSelections!.Where(candidate =>
                     candidate.State == TenantSessionSelectionIntentState.Finalized &&
                     candidate.FinalizationReceipt is null))
        {
            var audit = document.AuditEnvelopes.Single(item =>
                item.CorrelationId == intent.CorrelationId &&
                item.EventType == "WebTenantSelected");
            intent.FinalizationReceipt = new TenantSessionSelectionReceipt(
                document.TenantId,
                document.OwnerVersion,
                intent.MembershipId,
                audit.Sequence,
                audit.EnvelopeHash,
                intent.IntentDigest,
                intent.FinalizationHomeDecisionDigest
                    ?? throw InvalidAuthorityDocument());
        }
    }

    private static void SealSessionRevocationReceipts(TenantAuthorityDocument document)
    {
        foreach (var intent in document.SessionRevocations!.Where(candidate =>
                     candidate.State == TenantSessionRevocationIntentState.Finalized &&
                     candidate.FinalizationReceipt is null))
        {
            var audit = document.AuditEnvelopes.Single(item =>
                item.CorrelationId == intent.CorrelationId &&
                item.EventType == "WebUserSessionRevoked");
            intent.FinalizationReceipt = new TenantSessionRevocationReceipt(
                document.TenantId,
                document.OwnerVersion,
                intent.MembershipId,
                intent.SessionCorrelationId,
                audit.Sequence,
                audit.EnvelopeHash,
                intent.IntentDigest,
                intent.FinalizationHomeDecisionDigest
                    ?? throw InvalidAuthorityDocument());
        }
    }

    private static string ComputeTenantAuditEnvelopeHash(
        string tenantId,
        TenantIdentityAuditEnvelopeDocument envelope) =>
        InstallationAuditIntegrity.Hash(
            tenantId,
            envelope.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.CorrelationId,
            envelope.EventType,
            envelope.AccountId,
            envelope.ActorAccountId,
            envelope.AuthorityEvidenceDigest,
            envelope.MembershipId,
            envelope.PreviousHash,
            envelope.PayloadDigest,
            envelope.OccurredAtUtc.ToUnixTimeMilliseconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void ValidateIntegrity(TenantAuthorityDocument document)
    {
        var sessionSelections = document.SessionSelections ?? throw InvalidAuthorityDocument();
        var sessionRevocations = document.SessionRevocations ?? throw InvalidAuthorityDocument();
        if (document.Memberships.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count() !=
                document.Memberships.Count ||
            document.Intents.Select(item => item.CorrelationId).Distinct(StringComparer.Ordinal).Count() !=
                document.Intents.Count ||
            sessionSelections.Select(item => item.CorrelationId)
                .Distinct(StringComparer.Ordinal).Count() != sessionSelections.Count ||
            sessionRevocations.Select(item => item.CorrelationId)
                .Distinct(StringComparer.Ordinal).Count() != sessionRevocations.Count ||
            document.Memberships.Any(item =>
                !string.Equals(item.TenantId, document.TenantId, StringComparison.Ordinal)) ||
            document.Intents.Any(item =>
                !string.Equals(item.ProposedMembership.TenantId, document.TenantId, StringComparison.Ordinal)))
        {
            throw InvalidAuthorityDocument();
        }

        var previousHash = InstallationAuditIntegrity.ZeroHash;
        long sequence = 0;
        foreach (var envelope in document.AuditEnvelopes.OrderBy(item => item.Sequence))
        {
            sequence++;
            var expectedHash = ComputeTenantAuditEnvelopeHash(document.TenantId, envelope);
            if (envelope.Sequence != sequence ||
                !string.Equals(envelope.PreviousHash, previousHash, StringComparison.Ordinal) ||
                !string.Equals(envelope.EnvelopeHash, expectedHash, StringComparison.Ordinal))
            {
                throw InvalidAuthorityDocument();
            }
            previousHash = envelope.EnvelopeHash;
        }

        if (document.AuditHeadSequence != sequence ||
            !string.Equals(document.AuditHeadHash, previousHash, StringComparison.Ordinal))
        {
            throw InvalidAuthorityDocument();
        }

        foreach (var intent in document.Intents)
        {
            var membershipDigest = ComputeMembershipDigest(intent.ProposedMembership);
            var intentDigest = InstallationAuditIntegrity.Hash(
                intent.CorrelationId,
                intent.CommandFingerprint,
                intent.AccountId,
                intent.ActorAccountId,
                intent.AuthorityEvidenceDigest,
                membershipDigest);
            if (!string.Equals(intent.PayloadDigest, membershipDigest, StringComparison.Ordinal) ||
                !string.Equals(intent.IntentDigest, intentDigest, StringComparison.Ordinal))
            {
                throw InvalidAuthorityDocument();
            }

            if (intent.State != TenantMembershipIntentState.Finalized)
            {
                if (intent.FinalizationReceipt is not null ||
                    intent.FinalizationHomeDecisionDigest is not null ||
                    (intent.State == TenantMembershipIntentState.Prepared &&
                     intent.AbortHomeDecisionDigest is not null) ||
                    (intent.State == TenantMembershipIntentState.Aborted &&
                     string.IsNullOrWhiteSpace(intent.AbortHomeDecisionDigest)))
                {
                    throw InvalidAuthorityDocument();
                }
                continue;
            }

            var receipt = intent.FinalizationReceipt ?? throw InvalidAuthorityDocument();
            var audit = document.AuditEnvelopes.SingleOrDefault(item =>
                item.Sequence == receipt.AuditSequence &&
                string.Equals(item.CorrelationId, intent.CorrelationId, StringComparison.Ordinal));
            if (audit is null ||
                receipt.DocumentOwnerVersion <= 0 ||
                receipt.DocumentOwnerVersion > document.OwnerVersion ||
                !string.Equals(receipt.TenantId, document.TenantId, StringComparison.Ordinal) ||
                !string.Equals(receipt.MembershipId, intent.ProposedMembership.MembershipId,
                    StringComparison.Ordinal) ||
                receipt.MembershipOwnerVersion != intent.ProposedMembership.OwnerVersion ||
                !string.Equals(receipt.MembershipDigest, membershipDigest, StringComparison.Ordinal) ||
                !string.Equals(receipt.AuditHeadHash, audit.EnvelopeHash, StringComparison.Ordinal) ||
                !string.Equals(receipt.IntentDigest, intentDigest, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(intent.FinalizationHomeDecisionDigest) ||
                !string.Equals(
                    receipt.HomeDecisionDigest,
                    intent.FinalizationHomeDecisionDigest,
                    StringComparison.Ordinal))
            {
                throw InvalidAuthorityDocument();
            }
        }

        foreach (var intent in document.SessionSelections)
        {
            var intentDigest = InstallationAuditIntegrity.Hash(
                intent.CorrelationId,
                intent.CommandFingerprint,
                intent.AccountId,
                intent.MembershipId,
                intent.PayloadDigest);
            if (!string.Equals(intent.IntentDigest, intentDigest, StringComparison.Ordinal))
            {
                throw InvalidAuthorityDocument();
            }

            if (intent.State != TenantSessionSelectionIntentState.Finalized)
            {
                if (intent.FinalizationReceipt is not null ||
                    intent.FinalizationHomeDecisionDigest is not null ||
                    (intent.State == TenantSessionSelectionIntentState.Prepared &&
                     intent.AbortHomeDecisionDigest is not null) ||
                    (intent.State == TenantSessionSelectionIntentState.Aborted &&
                     string.IsNullOrWhiteSpace(intent.AbortHomeDecisionDigest)))
                {
                    throw InvalidAuthorityDocument();
                }
                continue;
            }

            var receipt = intent.FinalizationReceipt ?? throw InvalidAuthorityDocument();
            var audit = document.AuditEnvelopes.SingleOrDefault(item =>
                item.Sequence == receipt.AuditSequence &&
                item.CorrelationId == intent.CorrelationId &&
                item.EventType == "WebTenantSelected");
            if (audit is null ||
                receipt.DocumentOwnerVersion <= 0 ||
                receipt.DocumentOwnerVersion > document.OwnerVersion ||
                receipt.TenantId != document.TenantId ||
                receipt.MembershipId != intent.MembershipId ||
                receipt.AuditHeadHash != audit.EnvelopeHash ||
                receipt.IntentDigest != intentDigest ||
                string.IsNullOrWhiteSpace(intent.FinalizationHomeDecisionDigest) ||
                receipt.HomeDecisionDigest != intent.FinalizationHomeDecisionDigest)
            {
                throw InvalidAuthorityDocument();
            }
        }

        foreach (var intent in document.SessionRevocations)
        {
            var intentDigest = InstallationAuditIntegrity.Hash(
                intent.CorrelationId,
                intent.CommandFingerprint,
                intent.AccountId,
                intent.MembershipId,
                intent.SessionCorrelationId,
                intent.PayloadDigest);
            if (!string.Equals(intent.IntentDigest, intentDigest, StringComparison.Ordinal))
            {
                throw InvalidAuthorityDocument();
            }

            if (intent.State != TenantSessionRevocationIntentState.Finalized)
            {
                if (intent.FinalizationReceipt is not null ||
                    intent.FinalizationHomeDecisionDigest is not null ||
                    (intent.State == TenantSessionRevocationIntentState.Prepared &&
                     intent.AbortHomeDecisionDigest is not null) ||
                    (intent.State == TenantSessionRevocationIntentState.Aborted &&
                     string.IsNullOrWhiteSpace(intent.AbortHomeDecisionDigest)))
                {
                    throw InvalidAuthorityDocument();
                }
                continue;
            }

            var receipt = intent.FinalizationReceipt ?? throw InvalidAuthorityDocument();
            var audit = document.AuditEnvelopes.SingleOrDefault(item =>
                item.Sequence == receipt.AuditSequence &&
                item.CorrelationId == intent.CorrelationId &&
                item.EventType == "WebUserSessionRevoked");
            if (audit is null ||
                receipt.DocumentOwnerVersion <= 0 ||
                receipt.DocumentOwnerVersion > document.OwnerVersion ||
                receipt.TenantId != document.TenantId ||
                receipt.MembershipId != intent.MembershipId ||
                receipt.SessionCorrelationId != intent.SessionCorrelationId ||
                receipt.AuditHeadHash != audit.EnvelopeHash ||
                receipt.IntentDigest != intentDigest ||
                string.IsNullOrWhiteSpace(intent.FinalizationHomeDecisionDigest) ||
                receipt.HomeDecisionDigest != intent.FinalizationHomeDecisionDigest)
            {
                throw InvalidAuthorityDocument();
            }
        }


        foreach (var membership in document.Memberships)
        {
            var latest = document.Intents
                .Where(item =>
                    item.State == TenantMembershipIntentState.Finalized &&
                    string.Equals(item.AccountId, membership.AccountId, StringComparison.Ordinal))
                .OrderBy(item => item.FinalizationReceipt!.AuditSequence)
                .LastOrDefault();
            if (latest is null ||
                !string.Equals(
                    ComputeMembershipDigest(membership),
                    latest.PayloadDigest,
                    StringComparison.Ordinal))
            {
                throw InvalidAuthorityDocument();
            }
        }
    }

    private static InvalidOperationException InvalidAuthorityDocument() =>
        new("identity.tenant_authority_invalid: authority evidence failed integrity validation.");

    private static bool AbortSessionSelection(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        InstallationIdentityHomeDecisionReceipt homeDecision,
        DateTimeOffset occurredAtUtc)
    {
        var intent = document.SessionSelections!
            .SingleOrDefault(item => item.CorrelationId == correlationId);
        if (intent is null)
        {
            return false;
        }
        if (!string.Equals(intent.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.session_selection_changed_replay: correlation was reused.");
        }
        if (intent.State == TenantSessionSelectionIntentState.Aborted)
        {
            return false;
        }
        if (intent.State == TenantSessionSelectionIntentState.Finalized)
        {
            throw new InvalidOperationException(
                "identity.session_selection_committed: finalized selection cannot abort.");
        }

        intent.State = TenantSessionSelectionIntentState.Aborted;
        intent.AbortHomeDecisionDigest = homeDecision.DecisionDigest;
        intent.AbortedAtUtc = occurredAtUtc;
        return true;
    }

    private static bool AbortSessionRevocation(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        InstallationIdentityHomeDecisionReceipt homeDecision,
        DateTimeOffset occurredAtUtc)
    {
        var intent = document.SessionRevocations!
            .SingleOrDefault(item => item.CorrelationId == correlationId);
        if (intent is null)
        {
            return false;
        }
        if (!string.Equals(intent.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.session_revocation_changed_replay: correlation was reused.");
        }
        if (intent.State == TenantSessionRevocationIntentState.Aborted)
        {
            return false;
        }
        if (intent.State == TenantSessionRevocationIntentState.Finalized)
        {
            throw new InvalidOperationException(
                "identity.session_revocation_committed: finalized revocation cannot abort.");
        }

        intent.State = TenantSessionRevocationIntentState.Aborted;
        intent.AbortHomeDecisionDigest = homeDecision.DecisionDigest;
        intent.AbortedAtUtc = occurredAtUtc;
        return true;
    }

    private static bool Abort(
        TenantAuthorityDocument document,
        string correlationId,
        string commandFingerprint,
        InstallationIdentityHomeDecisionReceipt homeDecision,
        DateTimeOffset occurredAtUtc)
    {
        var intent = document.Intents.SingleOrDefault(item => item.CorrelationId == correlationId);
        if (intent is null)
        {
            return false;
        }
        RequireSameFingerprint(intent, commandFingerprint);
        if (intent.State == TenantMembershipIntentState.Aborted)
        {
            return false;
        }
        if (intent.State == TenantMembershipIntentState.Finalized)
        {
            throw new InvalidOperationException(
                "identity.membership_intent_committed: finalized authority cannot be aborted.");
        }

        intent.State = TenantMembershipIntentState.Aborted;
        intent.AbortHomeDecisionDigest = homeDecision.DecisionDigest;
        intent.AbortedAtUtc = occurredAtUtc;
        return true;
    }

    private static void ValidateEnvelopeIdentity(
        string correlationId,
        string commandFingerprint,
        string accountId,
        string actorAccountId,
        string authorityEvidenceDigest,
        TenantMembershipMutation mutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityEvidenceDigest);
        if (string.IsNullOrWhiteSpace(mutation.CanonicalPrincipalId) ||
            string.IsNullOrWhiteSpace(mutation.GrantId) ||
            correlationId.Length > 128 ||
            commandFingerprint.Length > 128 ||
            accountId.Length > 64 ||
            actorAccountId.Length > 64 ||
            authorityEvidenceDigest.Length != 64 ||
            mutation.CanonicalPrincipalId.Length > 256 ||
            mutation.GrantId.Length > 128 ||
            !Enum.IsDefined(mutation.TargetStatus) ||
            !Guid.TryParseExact(mutation.TenantId, "D", out _) ||
            mutation.ExpectedGrantOwnerVersion <= 0 ||
            mutation.ResultingGrantOwnerVersion is <= 0 ||
            mutation.AuthorizationEpoch <= 0 ||
            mutation.ResultingAuthorizationEpoch is <= 0 ||
            mutation.ExpectedMembershipOwnerVersion < 0)
        {
            throw new ArgumentException("Membership authority coordinates only canonical positive versions.");
        }
    }

    private static void RequireSameFingerprint(
        TenantMembershipIntentDocument intent,
        string commandFingerprint)
    {
        if (!string.Equals(intent.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.membership_changed_replay: correlation was already used by another command.");
        }
    }

    private static TenantMembershipSnapshot Project(TenantMembershipDocument row) =>
        new(
            row.MembershipId,
            row.AccountId,
            row.TenantId,
            row.CanonicalPrincipalId,
            row.GrantId,
            row.GrantOwnerVersion,
            row.AuthorizationEpoch,
            row.Status,
            row.OwnerVersion);

    private static string RandomHex(int byteCount) =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(byteCount));

    private sealed class TenantAuthorityDocument
    {
        public int SchemaVersion { get; set; }
        public required string TenantId { get; set; }
        public long OwnerVersion { get; set; }
        public required List<TenantMembershipDocument> Memberships { get; set; }
        public required List<TenantMembershipIntentDocument> Intents { get; set; }
        public List<TenantSessionSelectionIntentDocument>? SessionSelections { get; set; }
        public List<TenantSessionRevocationIntentDocument>? SessionRevocations { get; set; }
        public required List<TenantIdentityAuditEnvelopeDocument> AuditEnvelopes { get; set; }
        public long AuditHeadSequence { get; set; }
        public required string AuditHeadHash { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class TenantMembershipDocument
    {
        public required string MembershipId { get; set; }
        public required string AccountId { get; set; }
        public required string TenantId { get; set; }
        public required string CanonicalPrincipalId { get; set; }
        public required string GrantId { get; set; }
        public long GrantOwnerVersion { get; set; }
        public long AuthorizationEpoch { get; set; }
        public TenantMembershipStatus Status { get; set; }
        public long OwnerVersion { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class TenantMembershipIntentDocument
    {
        public required string CorrelationId { get; set; }
        public required string CommandFingerprint { get; set; }
        public required string AccountId { get; set; }
        public required string ActorAccountId { get; set; }
        public required string AuthorityEvidenceDigest { get; set; }
        public TenantMembershipIntentState State { get; set; }
        public required TenantMembershipDocument ProposedMembership { get; set; }
        public required string PayloadDigest { get; set; }
        public required string IntentDigest { get; set; }
        public TenantMembershipFinalizationReceipt? FinalizationReceipt { get; set; }
        public string? FinalizationHomeDecisionDigest { get; set; }
        public string? AbortHomeDecisionDigest { get; set; }
        public DateTimeOffset PreparedAtUtc { get; set; }
        public DateTimeOffset? FinalizedAtUtc { get; set; }
        public DateTimeOffset? AbortedAtUtc { get; set; }
    }

    private sealed class TenantSessionSelectionIntentDocument
    {
        public required string CorrelationId { get; set; }
        public required string CommandFingerprint { get; set; }
        public required string AccountId { get; set; }
        public required string MembershipId { get; set; }
        public required string PayloadDigest { get; set; }
        public required string IntentDigest { get; set; }
        public TenantSessionSelectionIntentState State { get; set; }
        public TenantSessionSelectionReceipt? FinalizationReceipt { get; set; }
        public string? FinalizationHomeDecisionDigest { get; set; }
        public string? AbortHomeDecisionDigest { get; set; }
        public DateTimeOffset PreparedAtUtc { get; set; }
        public DateTimeOffset? FinalizedAtUtc { get; set; }
        public DateTimeOffset? AbortedAtUtc { get; set; }
    }

    private sealed class TenantSessionRevocationIntentDocument
    {
        public required string CorrelationId { get; set; }
        public required string CommandFingerprint { get; set; }
        public required string AccountId { get; set; }
        public required string MembershipId { get; set; }
        public required string SessionCorrelationId { get; set; }
        public required string PayloadDigest { get; set; }
        public required string IntentDigest { get; set; }
        public TenantSessionRevocationIntentState State { get; set; }
        public TenantSessionRevocationReceipt? FinalizationReceipt { get; set; }
        public string? FinalizationHomeDecisionDigest { get; set; }
        public string? AbortHomeDecisionDigest { get; set; }
        public DateTimeOffset PreparedAtUtc { get; set; }
        public DateTimeOffset? FinalizedAtUtc { get; set; }
        public DateTimeOffset? AbortedAtUtc { get; set; }
    }

    private sealed class TenantIdentityAuditEnvelopeDocument
    {
        public long Sequence { get; set; }
        public required string CorrelationId { get; set; }
        public required string EventType { get; set; }
        public required string AccountId { get; set; }
        public required string ActorAccountId { get; set; }
        public required string AuthorityEvidenceDigest { get; set; }
        public required string MembershipId { get; set; }
        public required string PreviousHash { get; set; }
        public required string EnvelopeHash { get; set; }
        public required string PayloadDigest { get; set; }
        public DateTimeOffset OccurredAtUtc { get; set; }
    }
}
