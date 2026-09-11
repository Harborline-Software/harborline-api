using System.Data;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Lease;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Named evidence for the only two gate-free coordinator continuations. Ordinary live membership acts use
/// <see cref="AuthorizationWriteContext"/>; this type makes founder bootstrap and durable recovery explicit
/// at every call site and is fenced by the authorization architecture tests.
/// </summary>
internal sealed class InstallationIdentityCoordinatorContinuation
{
    private InstallationIdentityCoordinatorContinuation(string kind) => Kind = kind;
    internal string Kind { get; }
    private static readonly InstallationIdentityCoordinatorContinuation s_founderAttachment = new("founder-attachment");
    private static readonly InstallationIdentityCoordinatorContinuation s_recovery = new("coordinator-recovery");
    internal static InstallationIdentityCoordinatorContinuation FounderAttachment => s_founderAttachment;
    internal static InstallationIdentityCoordinatorContinuation Recovery => s_recovery;

    internal void RequireFounderAttachment()
    {
        if (!ReferenceEquals(this, s_founderAttachment))
            throw new ArgumentException("Only founder attachment may initiate a gate-free coordination.");
    }

    internal void RequireRecovery()
    {
        if (!ReferenceEquals(this, s_recovery))
            throw new ArgumentException("Only coordinator recovery may resume without a live gate decision.");
    }
}

internal enum InstallationIdentityCoordinationStatus
{
    Completed,
    IdempotentReplay,
    PendingRecovery,
    Aborted,
    ChangedReplay,
}

internal sealed record InstallationIdentityCoordinationCommand(
    string CorrelationId,
    string AccountId,
    string ActorAccountId,
    string AuthorityEvidenceDigest,
    long ExpectedAccountOwnerVersion,
    long ExpectedAccountSecurityVersion,
    long ExpectedActorOwnerVersion,
    long ExpectedActorSecurityVersion,
    IReadOnlyList<TenantMembershipMutation> Mutations);

internal sealed record InstallationIdentityCoordinationResult(
    InstallationIdentityCoordinationStatus Status,
    string CorrelationId,
    IReadOnlyList<TenantMembershipFinalizationReceipt> Receipts);

/// <summary>
/// Installation-home decision verifier consumed by every tenant authority store. Tenant stores
/// cannot finalize or abort from a caller assertion; they reread the durable R3-H decision here.
/// </summary>
internal sealed class InstallationIdentityHomeDecisionAuthority(
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> homeFactory)
    : IInstallationIdentityHomeDecisionAuthority
{
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _homeFactory =
        homeFactory ?? throw new ArgumentNullException(nameof(homeFactory));

    public Task<InstallationIdentityHomeDecisionReceipt> RequireFinalizationAsync(
        string correlationId,
        string commandFingerprint,
        string tenantId,
        CancellationToken cancellationToken) =>
        RequireAsync(correlationId, commandFingerprint, tenantId, allowAbort: false, cancellationToken);

    public Task<InstallationIdentityHomeDecisionReceipt> RequireAbortAsync(
        string correlationId,
        string commandFingerprint,
        string tenantId,
        CancellationToken cancellationToken) =>
        RequireAsync(correlationId, commandFingerprint, tenantId, allowAbort: true, cancellationToken);

    private async Task<InstallationIdentityHomeDecisionReceipt> RequireAsync(
        string correlationId,
        string commandFingerprint,
        string tenantId,
        bool allowAbort,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Coordinators.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CorrelationId == correlationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "identity.home_decision_missing: tenant transition requires a durable home decision.");
        // Every command type declared under Data/Identity/ MUST have an arm here; the default arm
        // admits only the coordinator's own TenantMembershipMutation schema, so an unregistered
        // command type throws identity.coordinator_payload_invalid at the first tenant-head
        // finalization. InstallationIdentityHomeDecisionRegistryArchTests is the drift canary.
        var admittedTenantIds = row.CommandType switch
        {
            WebTenantSelectionAuthority.CommandType =>
                WebTenantSelectionAuthority.ValidateStoredSelection(row),
            WebSelectedSessionLogoutAuthority.CommandType =>
                WebSelectedSessionLogoutAuthority.ValidateStoredLogout(row),
            WebTenantSwitchAuthority.CommandType =>
                WebTenantSwitchAuthority.ValidateStoredSwitchTenants(row),
            _ => InstallationIdentityCoordinatorService.ValidateStoredCoordinator(row)
                .Select(item => item.TenantId)
                .ToArray(),
        };
        if (!string.Equals(row.CommandFingerprint, commandFingerprint, StringComparison.Ordinal) ||
            !admittedTenantIds.Contains(tenantId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.home_decision_mismatch: tenant transition is outside the home decision.");
        }
        var stateAllowed = allowAbort
            ? row.State == InstallationIdentityCoordinatorState.Aborted
            : row.State is InstallationIdentityCoordinatorState.Committing or
                InstallationIdentityCoordinatorState.Finalizing or
                InstallationIdentityCoordinatorState.Completed;
        if (!stateAllowed)
        {
            throw new InvalidOperationException(allowAbort
                ? "identity.home_decision_commit_exists: abort is prohibited by the home decision."
                : "identity.home_decision_not_committed: finalization requires Committing.");
        }

        var digest = InstallationAuditIntegrity.Hash(
            row.CorrelationId,
            row.CommandFingerprint,
            row.State.ToString(),
            row.OwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.TenantIdsJson,
            row.FinalReceiptsJson);
        return new InstallationIdentityHomeDecisionReceipt(
            row.CorrelationId,
            row.CommandFingerprint,
            row.State,
            row.OwnerVersion,
            tenantId,
            digest);
    }
}

/// <summary>
/// R3-H coordinator. The installation store owns the decision and receipts; each tenant store owns
/// its membership and identity-audit document. Tenant selection consumes its live admission seam.
/// </summary>
internal sealed class InstallationIdentityCoordinatorService : IInvitationAcceptanceMembershipWriter
{
    private const string CommandType = "TenantMembershipMutation";
    private const int CoordinatorPayloadSchemaVersion = 2;
    private const string AbortCleanupPending = "identity.coordinator_abort_cleanup_pending";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _homeFactory;
    private readonly ITenantIdentityAuthorityPartitionResolver _partitions;
    private readonly ITenantMembershipAuthorityAdmission _admission;
    private readonly TimeProvider _timeProvider;
    private readonly IGrantStore? _grantStore;
    private readonly AuthorizationGate _authorizationGate;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public InstallationIdentityCoordinatorService(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> homeFactory,
        ITenantIdentityAuthorityPartitionResolver partitions,
        ITenantMembershipAuthorityAdmission admission,
        TimeProvider timeProvider,
        AuthorizationGate authorizationGate,
        IGrantStore? grantStore = null)
    {
        _homeFactory = homeFactory ?? throw new ArgumentNullException(nameof(homeFactory));
        _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _authorizationGate = authorizationGate ?? throw new ArgumentNullException(nameof(authorizationGate));
        _grantStore = grantStore;
    }

    /// <summary>
    /// Admits an ordinary live membership mutation before the coordinator opens its home decision store.
    /// Founder attachment and recovery resume use the context-free specialized methods below.
    /// </summary>
    internal async Task<InstallationIdentityCoordinationResult> ExecuteAsync(
        InstallationIdentityCoordinationCommand command,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Mutations.Count == 0 || command.Mutations.Any(item =>
                !string.Equals(item.TenantId, authority.Tenant.Value, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The membership mutations do not match the write authority tenant.", nameof(authority));

        var decision = await _authorizationGate.DecideAsync(
            authority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                "members",
                command.CorrelationId),
            cancellationToken).ConfigureAwait(false);
        decision.RequireAllowed();
        return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal Task<InstallationIdentityCoordinationResult> ExecuteAsync(
        InstallationIdentityCoordinationCommand command,
        AuthorizationWriteContext authority,
        AuthorizationDecision invitationBootstrapDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.AccountId != command.ActorAccountId || command.Mutations.Count == 0 ||
            command.Mutations.Any(item =>
                !string.Equals(item.TenantId, authority.Tenant.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(item.CanonicalPrincipalId, authority.Principal.Value, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Invitation onboarding must self-attach the minted principal in exactly one tenant.",
                nameof(command));
        }
        InvitationBootstrapDecisionValidation.RequireMembershipAdmission(
            invitationBootstrapDecision,
            command.AccountId,
            authority);
        return ExecuteCoreAsync(command, cancellationToken);
    }

    Task<InstallationIdentityCoordinationResult> IInvitationAcceptanceMembershipWriter.WriteAsync(
        InstallationIdentityCoordinationCommand command,
        AuthorizationWriteContext authority,
        AuthorizationDecision decision,
        CancellationToken cancellationToken) =>
        ExecuteAsync(command, authority, decision, cancellationToken);

    internal async Task<InstallationIdentityCoordinationResult> ExecuteAsync(
        InstallationIdentityCoordinationCommand command,
        InstallationIdentityCoordinatorContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        continuation.RequireFounderAttachment();
        return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallationIdentityCoordinationResult> ExecuteCoreAsync(
        InstallationIdentityCoordinationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var canonical = Canonicalize(command);
        var fingerprint = ComputeCommandFingerprint(canonical);
        var coordinator = await GetOrCreateHomeDecisionAsync(canonical, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        _ = ValidateStoredCoordinator(coordinator);
        if (!string.Equals(coordinator.CommandFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Result(InstallationIdentityCoordinationStatus.ChangedReplay, coordinator, []);
        }
        if (coordinator.State == InstallationIdentityCoordinatorState.Completed)
        {
            await ValidateCompletedEvidenceAsync(coordinator, cancellationToken).ConfigureAwait(false);
            await ApplyCompletedGrantMutationsAsync(coordinator, cancellationToken)
                .ConfigureAwait(false);
            return Result(
                InstallationIdentityCoordinationStatus.IdempotentReplay,
                coordinator,
                DeserializeReceipts(coordinator.FinalReceiptsJson));
        }

        return await ResumeCoreAsync(coordinator, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<InstallationIdentityCoordinationResult> ResumeAsync(
        string correlationId,
        InstallationIdentityCoordinatorContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        continuation.RequireRecovery();
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var coordinator = await context.Coordinators.AsNoTracking()
            .SingleAsync(item => item.CorrelationId == correlationId, cancellationToken)
            .ConfigureAwait(false);
        if (coordinator.State == InstallationIdentityCoordinatorState.Completed)
        {
            await ValidateCompletedEvidenceAsync(coordinator, cancellationToken).ConfigureAwait(false);
            await ApplyCompletedGrantMutationsAsync(coordinator, cancellationToken)
                .ConfigureAwait(false);
            return Result(
                InstallationIdentityCoordinationStatus.IdempotentReplay,
                coordinator,
                DeserializeReceipts(coordinator.FinalReceiptsJson));
        }

        return await ResumeCoreAsync(coordinator, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> IsAccountFencedAsync(
        string accountId,
        CancellationToken cancellationToken = default)
        => await IsAccountFencedAsync(accountId, excludedCorrelationId: null, cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> IsAccountFencedAsync(
        string accountId,
        string? excludedCorrelationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await context.Coordinators.AsNoTracking().AnyAsync(item =>
                item.AccountId == accountId &&
                (excludedCorrelationId == null || item.CorrelationId != excludedCorrelationId) &&
                (item.State == InstallationIdentityCoordinatorState.Preparing ||
                 item.State == InstallationIdentityCoordinatorState.Committing ||
                 item.State == InstallationIdentityCoordinatorState.Finalizing ||
                 (item.State == InstallationIdentityCoordinatorState.Aborted &&
                  item.FailureCode == AbortCleanupPending)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The server-side membership usability seam. Live admission validates Party/trust and grant
    /// freshness here before tenant selection may mint a session.
    /// </summary>
    internal async Task<TenantMembershipSnapshot?> ResolveUsableMembershipAsync(
        string accountId,
        string tenantId,
        CancellationToken cancellationToken = default)
        => await ResolveUsableMembershipAsync(
                accountId,
                tenantId,
                excludedCorrelationId: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<TenantMembershipSnapshot?> ResolveUsableMembershipAsync(
        string accountId,
        string tenantId,
        string? excludedCorrelationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var canonicalTenantId = Guid.Parse(tenantId).ToString("D");
        if (await IsAccountFencedAsync(accountId, excludedCorrelationId, cancellationToken)
            .ConfigureAwait(false))
        {
            return null;
        }
        await using (var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var accountIsActive = await context.Accounts.AsNoTracking().AnyAsync(item =>
                    item.AccountId == accountId && item.Status == InstallationAccountStatus.Active,
                cancellationToken)
                .ConfigureAwait(false);
            if (!accountIsActive)
            {
                return null;
            }
        }
        var partition = await _partitions.ResolveAsync(canonicalTenantId, cancellationToken)
            .ConfigureAwait(false);
        if (await partition.Memberships.IsAdmissionBlockedAsync(accountId, cancellationToken)
            .ConfigureAwait(false))
        {
            return null;
        }
        var membership = await partition.Memberships.GetMembershipAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        if (membership is null || membership.Status != TenantMembershipStatus.Active)
        {
            return null;
        }
        // Ticket 362 slice 3 — the one re-pin. The admission seam re-read the live grant row and the
        // live per-principal epoch; the snapshot every session mint pins (WebTenantSelectionAuthority,
        // WebTenantSwitchAuthority, WebSelectedSessionPrincipalAuthority) carries the CURRENT epoch, so
        // an administrator's narrowing or revocation no longer bricks the member's next login. It widens
        // nothing: the closure is re-derived from the live grants at every PEP read, the grant id and
        // owner-version teeth are still exact, the epoch can only move forward, and an ALREADY-minted
        // session keeps its own older pin and is refused (MatchesPinnedAuthority, and the resolver's
        // epoch fence) — which is exactly the invalidation the narrowing act intends.
        var liveAuthorizationEpoch = await _admission
            .ValidateExistingAsync(accountId, membership, cancellationToken)
            .ConfigureAwait(false);
        return await IsAccountFencedAsync(accountId, excludedCorrelationId, cancellationToken)
            .ConfigureAwait(false)
            ? null
            : membership with { AuthorizationEpoch = liveAuthorizationEpoch };
    }

    private async Task<InstallationIdentityCoordinationResult> ResumeCoreAsync(
        InstallationIdentityCoordinatorRecord coordinator,
        CancellationToken cancellationToken)
    {
        var mutations = ValidateStoredCoordinator(coordinator);
        var held = new List<HeldPartition>();
        try
        {
            foreach (var mutation in mutations.OrderBy(item => item.TenantId, StringComparer.Ordinal))
            {
                var partition = await _partitions.ResolveAsync(mutation.TenantId, cancellationToken)
                    .ConfigureAwait(false);
                var resourceId = $"identity.membership:{mutation.TenantId}";
                var lease = await partition.Leases.AcquireAsync(resourceId, LeaseDuration, cancellationToken)
                    .ConfigureAwait(false);
                if (lease is null)
                {
                    if (coordinator.State == InstallationIdentityCoordinatorState.Preparing)
                    {
                        coordinator = await MarkAbortedAsync(coordinator.CorrelationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    return Result(InstallationIdentityCoordinationStatus.PendingRecovery, coordinator, []);
                }
                held.Add(new HeldPartition(partition, lease));
            }

            if (coordinator.State == InstallationIdentityCoordinatorState.Aborted)
            {
                var cleaned = await AbortPreparedAsync(coordinator, mutations, held, cancellationToken)
                    .ConfigureAwait(false);
                if (cleaned)
                {
                    coordinator = await ClearAbortCleanupAsync(coordinator.CorrelationId, cancellationToken)
                        .ConfigureAwait(false);
                    return Result(InstallationIdentityCoordinationStatus.Aborted, coordinator, []);
                }
                return Result(InstallationIdentityCoordinationStatus.PendingRecovery, coordinator, []);
            }

            if (coordinator.State == InstallationIdentityCoordinatorState.Preparing)
            {
                foreach (var mutation in mutations)
                {
                    await _admission.ValidateMutationAsync(
                            coordinator.ActorAccountId,
                            coordinator.AuthorityEvidenceDigest,
                            coordinator.AccountId,
                            mutation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var partition = RequiredPartition(held, mutation.TenantId);
                    await partition.Memberships.PrepareAsync(
                            coordinator.CorrelationId,
                            coordinator.CommandFingerprint,
                            coordinator.AccountId,
                            coordinator.ActorAccountId,
                            coordinator.AuthorityEvidenceDigest,
                            mutation,
                            coordinator.CreatedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var mutation in mutations)
                {
                    await _admission.ValidateMutationAsync(
                            coordinator.ActorAccountId,
                            coordinator.AuthorityEvidenceDigest,
                            coordinator.AccountId,
                            mutation,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                coordinator = await CommitPreparedHomeDecisionAsync(
                        coordinator,
                        held,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var receipts = DeserializeReceipts(coordinator.FinalReceiptsJson).ToList();
            if (coordinator.State == InstallationIdentityCoordinatorState.Committing)
            {
                foreach (var mutation in mutations)
                {
                    EnsureLeasesCurrent(held);
                    var partition = RequiredPartition(held, mutation.TenantId);
                    var receipt = await partition.Memberships.FinalizeAsync(
                            coordinator.CorrelationId,
                            coordinator.CommandFingerprint,
                            _timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    receipts.RemoveAll(item => item.TenantId == receipt.TenantId);
                    receipts.Add(receipt);
                    coordinator = await PersistReceiptAsync(
                            coordinator.CorrelationId,
                            receipts,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                coordinator = await AdvanceHomeStateAsync(
                        coordinator.CorrelationId,
                        InstallationIdentityCoordinatorState.Committing,
                        InstallationIdentityCoordinatorState.Finalizing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (coordinator.State == InstallationIdentityCoordinatorState.Finalizing)
            {
                EnsureLeasesCurrent(held);
                await VerifyFinalReceiptsAsync(
                        coordinator,
                        mutations,
                        held,
                        cancellationToken)
                    .ConfigureAwait(false);
                coordinator = await CompleteWithInstallationAuditAsync(
                        coordinator.CorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (coordinator.State == InstallationIdentityCoordinatorState.Completed)
            {
                await ApplyCompletedGrantMutationsAsync(coordinator, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Result(
                InstallationIdentityCoordinationStatus.Completed,
                coordinator,
                DeserializeReceipts(coordinator.FinalReceiptsJson));
        }
        catch
        {
            coordinator = await ReloadHomeDecisionAsync(
                    coordinator.CorrelationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (coordinator.State == InstallationIdentityCoordinatorState.Completed)
            {
                await ApplyCompletedGrantMutationsAsync(coordinator, CancellationToken.None)
                    .ConfigureAwait(false);
                return Result(
                    InstallationIdentityCoordinationStatus.Completed,
                    coordinator,
                    DeserializeReceipts(coordinator.FinalReceiptsJson));
            }
            if (coordinator.State is
                InstallationIdentityCoordinatorState.Committing or
                InstallationIdentityCoordinatorState.Finalizing)
            {
                return Result(
                    InstallationIdentityCoordinationStatus.PendingRecovery,
                    coordinator,
                    DeserializeReceipts(coordinator.FinalReceiptsJson));
            }
            if (coordinator.State == InstallationIdentityCoordinatorState.Preparing)
            {
                coordinator = await MarkAbortedAsync(coordinator.CorrelationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            if (coordinator.State == InstallationIdentityCoordinatorState.Aborted &&
                coordinator.FailureCode is null)
            {
                return Result(InstallationIdentityCoordinationStatus.Aborted, coordinator, []);
            }

            var cleaned = await AbortPreparedAsync(
                    coordinator,
                    mutations,
                    held,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (cleaned)
            {
                coordinator = await ClearAbortCleanupAsync(
                        coordinator.CorrelationId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Result(InstallationIdentityCoordinationStatus.Aborted, coordinator, []);
            }
            return Result(InstallationIdentityCoordinationStatus.PendingRecovery, coordinator, []);
        }
        finally
        {
            foreach (var item in held.AsEnumerable().Reverse())
            {
                try
                {
                    await item.Partition.Leases.ReleaseAsync(item.Lease, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Lease expiry is the fail-safe release; a later resume reacquires before CAS.
                }
            }
        }
    }

    private async Task<InstallationIdentityCoordinatorRecord> GetOrCreateHomeDecisionAsync(
        InstallationIdentityCoordinationCommand command,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var existing = await context.Coordinators.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CorrelationId == command.CorrelationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var account = await context.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AccountId == command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null || account.Status != InstallationAccountStatus.Active ||
            account.OwnerVersion != command.ExpectedAccountOwnerVersion ||
            account.SecurityVersion != command.ExpectedAccountSecurityVersion)
        {
            throw new InvalidOperationException(
                "identity.account_version_stale: active account authority did not match.");
        }
        var actor = string.Equals(command.ActorAccountId, account.AccountId, StringComparison.Ordinal)
            ? account
            : await context.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.AccountId == command.ActorAccountId, cancellationToken)
                .ConfigureAwait(false);
        if (actor is null || actor.Status != InstallationAccountStatus.Active ||
            actor.OwnerVersion != command.ExpectedActorOwnerVersion ||
            actor.SecurityVersion != command.ExpectedActorSecurityVersion)
        {
            throw new InvalidOperationException(
                "identity.actor_authority_stale: initiating account is not active.");
        }

        var now = _timeProvider.GetUtcNow();
        var tenantIds = command.Mutations.Select(item => item.TenantId).ToArray();
        var row = new InstallationIdentityCoordinatorRecord
        {
            CorrelationId = command.CorrelationId,
            CommandType = CommandType,
            CommandFingerprint = fingerprint,
            PayloadSchemaVersion = CoordinatorPayloadSchemaVersion,
            AccountId = command.AccountId,
            ActorAccountId = command.ActorAccountId,
            AuthorityEvidenceDigest = command.AuthorityEvidenceDigest,
            ExpectedAccountOwnerVersion = command.ExpectedAccountOwnerVersion,
            ExpectedAccountSecurityVersion = command.ExpectedAccountSecurityVersion,
            ExpectedActorOwnerVersion = command.ExpectedActorOwnerVersion,
            ExpectedActorSecurityVersion = command.ExpectedActorSecurityVersion,
            TenantIdsJson = JsonSerializer.Serialize(tenantIds, JsonOptions),
            IntentPayloadJson = JsonSerializer.Serialize(command.Mutations, JsonOptions),
            FinalReceiptsJson = "[]",
            State = InstallationIdentityCoordinatorState.Preparing,
            FailureCode = null,
            OwnerVersion = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Coordinators.Add(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
        catch (DbUpdateException exception) when (IsUniquenessConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return await context.Coordinators.AsNoTracking()
                .SingleAsync(item => item.CorrelationId == command.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<InstallationIdentityCoordinatorRecord> ReloadHomeDecisionAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await context.Coordinators.AsNoTracking()
            .SingleAsync(item => item.CorrelationId == correlationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InstallationIdentityCoordinatorRecord> MarkAbortedAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State != InstallationIdentityCoordinatorState.Preparing &&
            row.State != InstallationIdentityCoordinatorState.Aborted)
        {
            throw new InvalidOperationException(
                "identity.coordinator_commit_decided: abort is prohibited after Committing.");
        }
        row.State = InstallationIdentityCoordinatorState.Aborted;
        row.FailureCode = AbortCleanupPending;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<InstallationIdentityCoordinatorRecord> ClearAbortCleanupAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State != InstallationIdentityCoordinatorState.Aborted)
        {
            throw new InvalidOperationException("identity.coordinator_state_invalid: expected Aborted.");
        }
        row.FailureCode = null;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<InstallationIdentityCoordinatorRecord> AdvanceHomeStateAsync(
        string correlationId,
        InstallationIdentityCoordinatorState expected,
        InstallationIdentityCoordinatorState next,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State == next)
        {
            return row;
        }
        if (row.State != expected)
        {
            throw new InvalidOperationException(
                $"identity.coordinator_state_invalid: expected {expected}, observed {row.State}.");
        }
        row.State = next;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<InstallationIdentityCoordinatorRecord> PersistReceiptAsync(
        string correlationId,
        IReadOnlyList<TenantMembershipFinalizationReceipt> receipts,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State != InstallationIdentityCoordinatorState.Committing)
        {
            throw new InvalidOperationException("identity.coordinator_state_invalid: receipt requires Committing.");
        }
        row.FinalReceiptsJson = JsonSerializer.Serialize(
            receipts.OrderBy(item => item.TenantId, StringComparer.Ordinal),
            JsonOptions);
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<InstallationIdentityCoordinatorRecord> CompleteWithInstallationAuditAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State == InstallationIdentityCoordinatorState.Completed)
        {
            return row;
        }
        if (row.State != InstallationIdentityCoordinatorState.Finalizing)
        {
            throw new InvalidOperationException("identity.coordinator_state_invalid: expected Finalizing.");
        }

        var tenants = JsonSerializer.Deserialize<string[]>(row.TenantIdsJson, JsonOptions) ?? [];
        var receipts = DeserializeReceipts(row.FinalReceiptsJson);
        if (receipts.Count != tenants.Length ||
            !receipts.Select(item => item.TenantId)
                .SequenceEqual(tenants, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.coordinator_receipts_incomplete: every tenant requires a canonical receipt.");
        }

        var identity = await context.InstallationIdentities.SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var root = await context.RootKeyEpochs.SingleAsync(
            item => item.InstallationIdentityId == identity.InstallationIdentityId &&
                    item.EpochNumber == identity.ActiveRootEpoch,
            cancellationToken).ConfigureAwait(false);
        var head = await context.AuditHeads.SingleAsync(
            item => item.InstallationIdentityId == identity.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(item => item.InstallationIdentityId == identity.InstallationIdentityId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root.Status != InstallationRootEpochStatus.Active ||
            !InstallationAuditIntegrity.HasValidChain(
                chain,
                head,
                identity.InstallationIdentityId))
        {
            throw new InvalidOperationException(
                "identity.coordinator_audit_invalid: installation audit or root authority is invalid.");
        }
        var now = _timeProvider.GetUtcNow();
        var envelope = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = identity.InstallationIdentityId,
            Sequence = head.Sequence + 1,
            CorrelationId = row.CorrelationId,
            CommandFingerprint = row.CommandFingerprint,
            EventType = "TenantMembershipCoordinationCompleted",
            ActorKind = "installation-account",
            ActorId = row.ActorAccountId,
            RootEpoch = root.EpochNumber,
            RootPublicKeyFingerprint = root.RootPublicKeyFingerprint,
            PreviousHash = head.HeadHash,
            PayloadDigest = InstallationAuditIntegrity.Hash(
                row.AccountId,
                row.ActorAccountId,
                row.AuthorityEvidenceDigest,
                row.IntentPayloadJson,
                row.FinalReceiptsJson),
            EnvelopeHash = string.Empty,
            OccurredAtUtc = now,
        };
        envelope.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(envelope);
        context.AuditEnvelopes.Add(envelope);
        head.Sequence = envelope.Sequence;
        head.HeadHash = envelope.EnvelopeHash;
        head.OwnerVersion++;
        head.UpdatedAtUtc = now;
        row.State = InstallationIdentityCoordinatorState.Completed;
        row.FailureCode = null;
        row.OwnerVersion++;
        row.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private static async Task<bool> AbortPreparedAsync(
        InstallationIdentityCoordinatorRecord coordinator,
        IReadOnlyList<TenantMembershipMutation> mutations,
        IReadOnlyList<HeldPartition> held,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var mutation in mutations)
        {
            try
            {
                await RequiredPartition(held, mutation.TenantId).Memberships.AbortAsync(
                        coordinator.CorrelationId,
                        coordinator.CommandFingerprint,
                        coordinator.UpdatedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                succeeded = false;
            }
        }
        return succeeded;
    }

    private static async Task VerifyFinalReceiptsAsync(
        InstallationIdentityCoordinatorRecord coordinator,
        IReadOnlyList<TenantMembershipMutation> mutations,
        IReadOnlyList<HeldPartition> held,
        CancellationToken cancellationToken)
    {
        var durable = DeserializeReceipts(coordinator.FinalReceiptsJson);
        foreach (var mutation in mutations)
        {
            var observed = await RequiredPartition(held, mutation.TenantId).Memberships.FinalizeAsync(
                    coordinator.CorrelationId,
                    coordinator.CommandFingerprint,
                    coordinator.UpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            var receipt = durable.SingleOrDefault(item => item.TenantId == mutation.TenantId);
            if (receipt is null || receipt != observed)
            {
                throw new InvalidOperationException(
                    "identity.coordinator_receipt_mismatch: tenant receipt evidence changed.");
            }
        }
    }

    /// <summary>
    /// The R3-H commit boundary. The authority rereads and Preparing-to-Committing state change
    /// share one serializable transaction, so a stale target/actor, root, audit chain, or home row
    /// cannot produce a commit decision.
    /// </summary>
    private async Task<InstallationIdentityCoordinatorRecord> CommitPreparedHomeDecisionAsync(
        InstallationIdentityCoordinatorRecord coordinator,
        IReadOnlyList<HeldPartition> held,
        CancellationToken cancellationToken)
    {
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var durable = await context.Coordinators.SingleAsync(
                item => item.CorrelationId == coordinator.CorrelationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (durable.State == InstallationIdentityCoordinatorState.Committing)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return durable;
        }
        if (durable.State != InstallationIdentityCoordinatorState.Preparing ||
            durable.OwnerVersion != coordinator.OwnerVersion)
        {
            throw new InvalidOperationException(
                "identity.coordinator_state_invalid: coordinator changed before the commit decision.");
        }
        _ = ValidateStoredCoordinator(durable);

        var account = await context.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AccountId == durable.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null || account.Status != InstallationAccountStatus.Active ||
            account.OwnerVersion != durable.ExpectedAccountOwnerVersion ||
            account.SecurityVersion != durable.ExpectedAccountSecurityVersion)
        {
            throw new InvalidOperationException(
                "identity.account_version_stale: account changed before the commit decision.");
        }
        var actor = string.Equals(durable.ActorAccountId, durable.AccountId, StringComparison.Ordinal)
            ? account
            : await context.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.AccountId == durable.ActorAccountId, cancellationToken)
                .ConfigureAwait(false);
        if (actor is null || actor.Status != InstallationAccountStatus.Active ||
            actor.OwnerVersion != durable.ExpectedActorOwnerVersion ||
            actor.SecurityVersion != durable.ExpectedActorSecurityVersion)
        {
            throw new InvalidOperationException(
                "identity.actor_authority_stale: initiating account changed before the commit decision.");
        }

        var identity = await context.InstallationIdentities.AsNoTracking()
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var root = await context.RootKeyEpochs.AsNoTracking()
            .SingleAsync(item =>
                item.InstallationIdentityId == identity.InstallationIdentityId &&
                item.EpochNumber == identity.ActiveRootEpoch,
                cancellationToken)
            .ConfigureAwait(false);
        var head = await context.AuditHeads.AsNoTracking()
            .SingleAsync(item => item.InstallationIdentityId == identity.InstallationIdentityId,
                cancellationToken)
            .ConfigureAwait(false);
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(item => item.InstallationIdentityId == identity.InstallationIdentityId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root.Status != InstallationRootEpochStatus.Active ||
            !InstallationAuditIntegrity.HasValidChain(chain, head, identity.InstallationIdentityId))
        {
            throw new InvalidOperationException(
                "identity.coordinator_audit_invalid: installation audit or root authority is invalid.");
        }

        EnsureLeasesCurrent(held);
        durable.State = InstallationIdentityCoordinatorState.Committing;
        durable.OwnerVersion++;
        durable.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return durable;
    }

    private async Task ValidateCompletedEvidenceAsync(
        InstallationIdentityCoordinatorRecord coordinator,
        CancellationToken cancellationToken)
    {
        var mutations = ValidateStoredCoordinator(coordinator);
        var receipts = DeserializeReceipts(coordinator.FinalReceiptsJson);
        await using (var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var identity = await context.InstallationIdentities.AsNoTracking()
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            var head = await context.AuditHeads.AsNoTracking()
                .SingleAsync(item => item.InstallationIdentityId == identity.InstallationIdentityId,
                    cancellationToken)
                .ConfigureAwait(false);
            var chain = await context.AuditEnvelopes.AsNoTracking()
                .Where(item => item.InstallationIdentityId == identity.InstallationIdentityId)
                .OrderBy(item => item.Sequence)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var completion = chain.SingleOrDefault(item =>
                string.Equals(item.CorrelationId, coordinator.CorrelationId, StringComparison.Ordinal));
            var expectedPayload = InstallationAuditIntegrity.Hash(
                coordinator.AccountId,
                coordinator.ActorAccountId,
                coordinator.AuthorityEvidenceDigest,
                coordinator.IntentPayloadJson,
                coordinator.FinalReceiptsJson);
            if (completion is null ||
                !string.Equals(completion.EventType, "TenantMembershipCoordinationCompleted",
                    StringComparison.Ordinal) ||
                !string.Equals(completion.CommandFingerprint, coordinator.CommandFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(completion.PayloadDigest, expectedPayload, StringComparison.Ordinal) ||
                !InstallationAuditIntegrity.HasValidChain(chain, head, identity.InstallationIdentityId))
            {
                throw new InvalidOperationException(
                    "identity.coordinator_evidence_invalid: completed home evidence is not authenticated.");
            }
        }

        var held = new List<HeldPartition>();
        try
        {
            foreach (var mutation in mutations)
            {
                var partition = await _partitions.ResolveAsync(mutation.TenantId, cancellationToken)
                    .ConfigureAwait(false);
                var resourceId = $"identity.membership:{mutation.TenantId}";
                var lease = await partition.Leases.AcquireAsync(
                        resourceId,
                        LeaseDuration,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "identity.coordinator_evidence_unavailable: tenant receipt lease is unavailable.");
                held.Add(new HeldPartition(partition, lease));
            }
            await VerifyFinalReceiptsAsync(
                    coordinator,
                    mutations,
                    held,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var item in held.AsEnumerable().Reverse())
            {
                await item.Partition.Leases.ReleaseAsync(item.Lease, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static InstallationIdentityCoordinationCommand Canonicalize(
        InstallationIdentityCoordinationCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ActorAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AuthorityEvidenceDigest);
        if (command.CorrelationId.Length > 128 ||
            command.AccountId.Length > 64 ||
            command.ActorAccountId.Length > 64 ||
            command.AuthorityEvidenceDigest.Length != 64 ||
            command.ExpectedAccountOwnerVersion <= 0 ||
            command.ExpectedAccountSecurityVersion <= 0 ||
            command.ExpectedActorOwnerVersion <= 0 ||
            command.ExpectedActorSecurityVersion <= 0 ||
            command.Mutations.Count == 0 ||
            command.Mutations.Count > 64)
        {
            throw new ArgumentException("Identity coordination requires positive account versions and mutations.");
        }
        if (string.Equals(command.AccountId, command.ActorAccountId, StringComparison.Ordinal) &&
            (command.ExpectedAccountOwnerVersion != command.ExpectedActorOwnerVersion ||
             command.ExpectedAccountSecurityVersion != command.ExpectedActorSecurityVersion))
        {
            throw new ArgumentException(
                "Identity coordination requires one account's target and actor versions to agree.");
        }
        var mutations = command.Mutations
            .Select(item =>
            {
                IReadOnlyList<string>? requestedPermissions = item.RequestedPermissions is null
                    ? null
                    : PermissionSet.Of(item.RequestedPermissions.ToArray()).Permissions
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                return item with
                {
                    TenantId = Guid.Parse(item.TenantId).ToString("D"),
                    RequestedPermissions = requestedPermissions,
                };
            })
            .OrderBy(item => item.TenantId, StringComparer.Ordinal)
            .ToArray();
        foreach (var mutation in mutations)
        {
            if (mutation.RequestedPermissions is null
                ? mutation.ResultingGrantOwnerVersion is not null || mutation.ResultingAuthorizationEpoch is not null
                : mutation.ResultingGrantOwnerVersion != mutation.ExpectedGrantOwnerVersion + 1 ||
                  mutation.ResultingAuthorizationEpoch != mutation.AuthorizationEpoch + 1)
            {
                throw new ArgumentException(
                    "Identity coordination requires a fenced resulting grant version for permission updates.");
            }
        }
        if (mutations.Select(item => item.TenantId).Distinct(StringComparer.Ordinal).Count() != mutations.Length)
        {
            throw new ArgumentException("One coordination command may mutate each tenant at most once.");
        }
        return command with { Mutations = mutations };
    }

    internal static IReadOnlyList<TenantMembershipMutation> ValidateStoredCoordinator(
        InstallationIdentityCoordinatorRecord coordinator)
    {
        if (coordinator.PayloadSchemaVersion != CoordinatorPayloadSchemaVersion ||
            !string.Equals(coordinator.CommandType, CommandType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.coordinator_payload_invalid: command type is not admitted.");
        }
        var stored = new InstallationIdentityCoordinationCommand(
            coordinator.CorrelationId,
            coordinator.AccountId,
            coordinator.ActorAccountId,
            coordinator.AuthorityEvidenceDigest,
            coordinator.ExpectedAccountOwnerVersion,
            coordinator.ExpectedAccountSecurityVersion,
            coordinator.ExpectedActorOwnerVersion,
            coordinator.ExpectedActorSecurityVersion,
            DeserializeMutations(coordinator.IntentPayloadJson));
        var canonical = Canonicalize(stored);
        var tenantIds = JsonSerializer.Deserialize<string[]>(coordinator.TenantIdsJson, JsonOptions) ?? [];
        var expectedTenantIds = canonical.Mutations.Select(item => item.TenantId).ToArray();
        if (!string.Equals(
                ComputeCommandFingerprint(canonical),
                coordinator.CommandFingerprint,
                StringComparison.Ordinal) ||
            !tenantIds.SequenceEqual(expectedTenantIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.coordinator_payload_invalid: durable command evidence was changed.");
        }
        return canonical.Mutations;
    }

    private void EnsureLeasesCurrent(IReadOnlyList<HeldPartition> held)
    {
        var now = _timeProvider.GetUtcNow();
        if (held.Any(item =>
                item.Lease.ExpiresAt <= now ||
                !item.Partition.Leases.Holds(item.Lease.ResourceId)))
        {
            throw new InvalidOperationException(
                "identity.coordinator_lease_stale: tenant lease expired before the next authority write.");
        }
    }

    private static string ComputeCommandFingerprint(InstallationIdentityCoordinationCommand command)
    {
        var values = new List<string>
        {
            CommandType,
            command.CorrelationId,
            command.AccountId,
            command.ActorAccountId,
            command.AuthorityEvidenceDigest,
            command.ExpectedAccountOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.ExpectedAccountSecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.ExpectedActorOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.ExpectedActorSecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var item in command.Mutations)
        {
            values.AddRange([
                item.TenantId,
                item.CanonicalPrincipalId,
                item.GrantId,
                item.ExpectedGrantOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.AuthorizationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ExpectedMembershipOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.TargetStatus.ToString(),
            ]);
            if (item.RequestedPermissions is not null)
            {
                values.Add(item.ResultingGrantOwnerVersion!.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                values.Add(item.ResultingAuthorizationEpoch!.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                values.Add(string.Join(",", item.RequestedPermissions));
            }
        }
        return InstallationAuditIntegrity.Hash([.. values]);
    }

    private async Task ApplyGrantMutationAsync(
        string correlationId,
        TenantMembershipMutation mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.RequestedPermissions is null)
        {
            return;
        }
        throw new InvalidOperationException(
            "identity.grant_permission_mutation_retired: role, subject, and source are immutable; issue a new role grant.");
    }

    private async Task ApplyCompletedGrantMutationsAsync(
        InstallationIdentityCoordinatorRecord coordinator,
        CancellationToken cancellationToken)
    {
        if (coordinator.State != InstallationIdentityCoordinatorState.Completed)
        {
            throw new InvalidOperationException(
                "identity.grant_mutation_requires_audit: grant mutation requires a completed audit.");
        }

        foreach (var mutation in ValidateStoredCoordinator(coordinator))
        {
            await ApplyGrantMutationAsync(
                    coordinator.CorrelationId,
                    mutation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<TenantMembershipMutation> DeserializeMutations(string json) =>
        JsonSerializer.Deserialize<TenantMembershipMutation[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("identity.coordinator_payload_invalid: mutations are missing.");

    private static IReadOnlyList<TenantMembershipFinalizationReceipt> DeserializeReceipts(string json) =>
        JsonSerializer.Deserialize<TenantMembershipFinalizationReceipt[]>(json, JsonOptions) ?? [];

    private static TenantIdentityAuthorityPartition RequiredPartition(
        IReadOnlyList<HeldPartition> held,
        string tenantId) =>
        held.Single(item => item.Partition.TenantId == tenantId).Partition;

    private static InstallationIdentityCoordinationResult Result(
        InstallationIdentityCoordinationStatus status,
        InstallationIdentityCoordinatorRecord coordinator,
        IReadOnlyList<TenantMembershipFinalizationReceipt> receipts) =>
        new(status, coordinator.CorrelationId, receipts);

    private static bool IsUniquenessConflict(Exception exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private sealed record HeldPartition(TenantIdentityAuthorityPartition Partition, Lease Lease);
}

/// <summary>
/// Bounded restart scanner for R3-H decisions. The live web host invokes this service through its
/// hosted recovery daemon after canonical tenant admission exists; this service itself advertises no
/// readiness and can recover every durable nonterminal row without desktop active-team state.
/// </summary>
internal sealed class InstallationIdentityCoordinatorRecoveryService(
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> homeFactory,
    InstallationIdentityCoordinatorService coordinator,
    ILogger<InstallationIdentityCoordinatorRecoveryService>? logger = null)
{
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _homeFactory =
        homeFactory ?? throw new ArgumentNullException(nameof(homeFactory));
    private readonly InstallationIdentityCoordinatorService _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly ILogger<InstallationIdentityCoordinatorRecoveryService>? _logger = logger;

    internal async Task<IReadOnlyList<InstallationIdentityCoordinationResult>> RecoverPendingAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        await using var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var correlationIds = await context.Coordinators.AsNoTracking()
            .Where(item =>
                item.CommandType == "TenantMembershipMutation" &&
                (item.State == InstallationIdentityCoordinatorState.Preparing ||
                 item.State == InstallationIdentityCoordinatorState.Committing ||
                 item.State == InstallationIdentityCoordinatorState.Finalizing ||
                 (item.State == InstallationIdentityCoordinatorState.Aborted && item.FailureCode != null)))
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.CorrelationId)
            .Select(item => item.CorrelationId)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<InstallationIdentityCoordinationResult>(correlationIds.Length);
        foreach (var correlationId in correlationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await _coordinator.ResumeAsync(
                        correlationId,
                        InstallationIdentityCoordinatorContinuation.Recovery,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger?.LogError(
                    exception,
                    "Identity coordinator recovery row {CorrelationId} failed; continuing the drain.",
                    correlationId);
            }
        }
        return results;
    }
}
