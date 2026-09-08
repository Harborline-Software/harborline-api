using System.Buffers.Text;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Session;
using Harborline.Api.Kernel.Lease;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Switches one selected session to a different tenant without a half-visible state.</summary>
public interface IWebTenantSwitchAuthority
{
    Task<WebTenantSelectionResult?> SwitchAsync(
        string? selectedHandle,
        string? requestedTenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates the old-tenant revoke, new-tenant selection, and installation audit head. The old
/// handle remains live and the destination handle remains absent until every audit owner has
/// durably finalized. One final session-store transaction then makes the rotation visible.
/// </summary>
internal sealed class WebTenantSwitchAuthority : IWebTenantSwitchAuthority
{
    internal const string CommandType = "WebTenantSwitch";
    internal const int PayloadSchemaVersion = 1;
    internal const string ReasonCode = "tenant-switch";

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _sessionFactory;
    private readonly WebSelectedSessionStore _sessions;
    private readonly IInstallationTenantCandidateLocator _candidateLocator;
    private readonly InstallationIdentityCoordinatorService _membershipAuthority;
    private readonly ITenantIdentityAuthorityPartitionResolver _partitions;
    private readonly ICanonicalPrincipalPartyReader _partyReader;
    private readonly SessionOptions _sessionOptions;
    private readonly TimeProvider _timeProvider;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public WebTenantSwitchAuthority(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
        WebSelectedSessionStore sessions,
        IInstallationTenantCandidateLocator candidateLocator,
        InstallationIdentityCoordinatorService membershipAuthority,
        ITenantIdentityAuthorityPartitionResolver partitions,
        ICanonicalPrincipalPartyReader partyReader,
        IOptions<SessionOptions> sessionOptions,
        TimeProvider timeProvider)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _candidateLocator = candidateLocator ?? throw new ArgumentNullException(nameof(candidateLocator));
        _membershipAuthority = membershipAuthority
            ?? throw new ArgumentNullException(nameof(membershipAuthority));
        _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        _partyReader = partyReader ?? throw new ArgumentNullException(nameof(partyReader));
        _sessionOptions = sessionOptions?.Value
            ?? throw new ArgumentNullException(nameof(sessionOptions));
        _sessionOptions.Validate();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WebTenantSelectionResult?> SwitchAsync(
        string? selectedHandle,
        string? requestedTenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedHandle) ||
            !Guid.TryParse(requestedTenantId, out var parsedTenant))
        {
            return null;
        }
        var targetTenantId = parsedTenant.ToString("D");
        var oldSession = await _sessions.FindStoredAsync(Digest(selectedHandle), cancellationToken)
            .ConfigureAwait(false);
        if (oldSession is null ||
            string.Equals(oldSession.TenantId, targetTenantId, StringComparison.Ordinal))
        {
            return null;
        }

        var correlationId = CorrelationIdFor(oldSession.SessionCorrelationId, targetTenantId);
        var existingHome = await FindHomeAsync(correlationId, cancellationToken).ConfigureAwait(false);
        SwitchPayload payload;
        if (existingHome is null)
        {
            if (!await RequireCurrentActiveSessionAsync(
                    oldSession,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return null;
            }
            var target = await ResolveTargetAuthorityAsync(
                    oldSession,
                    targetTenantId,
                    correlationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is null ||
                !await OldAuthorityIsCurrentAsync(oldSession, correlationId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }
            payload = new SwitchPayload(
                oldSession.TenantId,
                oldSession.MembershipId,
                oldSession.SessionCorrelationId,
                oldSession.HandleDigest,
                oldSession.AccountSecurityVersion,
                target);
        }
        else
        {
            payload = ValidateStoredSwitch(existingHome);
            if (!string.Equals(payload.Target.Membership.TenantId, targetTenantId,
                    StringComparison.Ordinal) ||
                !string.Equals(payload.OldSessionCorrelationId, oldSession.SessionCorrelationId,
                    StringComparison.Ordinal) ||
                !string.Equals(payload.OldHandleDigest, oldSession.HandleDigest,
                    StringComparison.Ordinal))
            {
                return null;
            }
        }

        var payloadDigest = ComputePayloadDigest(oldSession.AccountId, payload);
        var fingerprint = InstallationAuditIntegrity.Hash(CommandType, correlationId, payloadDigest);
        var home = existingHome ?? await CreateHomeAsync(
                correlationId,
                oldSession,
                payload,
                payloadDigest,
                fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        payload = ValidateStoredSwitch(home);
        if (!string.Equals(home.CommandFingerprint, fingerprint, StringComparison.Ordinal) ||
            home.State == InstallationIdentityCoordinatorState.Aborted)
        {
            return null;
        }

        var partitions = await ResolvePartitionsAsync(payload, cancellationToken).ConfigureAwait(false);
        var heldLeases = await AcquireLeasesAsync(partitions, cancellationToken).ConfigureAwait(false);
        if (heldLeases is null)
        {
            return null;
        }

        try
        {
            if (home.State == InstallationIdentityCoordinatorState.Completed)
            {
                var completedReceipts = DeserializeReceipts(home.FinalReceiptsJson);
                await RequireDurableTenantReceiptsAsync(
                        partitions,
                        home,
                        payload,
                        completedReceipts,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await RotateSessionsAsync(
                        oldSession,
                        payload,
                        home.CorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (home.State == InstallationIdentityCoordinatorState.Preparing)
            {
                await partitions.Old.Memberships.PrepareSessionRevocationAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        oldSession.AccountId,
                        payload.OldMembershipId,
                        payload.OldSessionCorrelationId,
                        payloadDigest,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await partitions.Target.Memberships.PrepareSessionSelectionAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        oldSession.AccountId,
                        payload.Target.Membership.MembershipId,
                        payloadDigest,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);

                var currentTarget = await ResolveTargetAuthorityAsync(
                        oldSession,
                        payload.Target.Membership.TenantId,
                        home.CorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!await RequireCurrentActiveSessionAsync(
                        oldSession,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false) ||
                    !await OldAuthorityIsCurrentAsync(
                        oldSession,
                        home.CorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false) ||
                    currentTarget is null ||
                    !Equals(currentTarget, payload.Target))
                {
                    await AbortPreparingAsync(home, partitions, cancellationToken)
                        .ConfigureAwait(false);
                    return null;
                }

                home = await AdvanceHomeAsync(
                        home.CorrelationId,
                        InstallationIdentityCoordinatorState.Preparing,
                        InstallationIdentityCoordinatorState.Committing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (home.State == InstallationIdentityCoordinatorState.Committing)
            {
                var receipts = await FinalizeTenantHeadsAsync(
                        partitions,
                        home,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
                home = await PersistReceiptsAndFinalizeAsync(
                        home.CorrelationId,
                        receipts,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (home.State != InstallationIdentityCoordinatorState.Finalizing)
            {
                return null;
            }
            var finalReceipts = DeserializeReceipts(home.FinalReceiptsJson);
            await RequireDurableTenantReceiptsAsync(
                    partitions,
                    home,
                    payload,
                    finalReceipts,
                    cancellationToken)
                .ConfigureAwait(false);

            home = await CompleteWithInstallationAuditAsync(home.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
            if (home.State != InstallationIdentityCoordinatorState.Completed)
            {
                return null;
            }

            // Audit completion and session visibility are deliberately separate stores. The
            // coordinator is restart-safe at Completed; a crash before this final transaction
            // leaves the old session live and a retry performs the one atomic rotation.
            return await RotateSessionsAsync(
                    oldSession,
                    payload,
                    home.CorrelationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await ReleaseLeasesAsync(heldLeases).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Home-decision arm for <see cref="CommandType"/>. Validates the durable switch payload and
    /// returns the tenant ids the decision admits — the same ordinal-ordered pair
    /// <see cref="CreateHomeAsync"/> writes to
    /// <see cref="InstallationIdentityCoordinatorRecord.TenantIdsJson"/>, which
    /// <see cref="ValidateStoredSwitch"/> has just proven the row still carries. Registered in
    /// <see cref="InstallationIdentityHomeDecisionAuthority"/>; without it a switch falls to the
    /// coordinator-mutation default arm and every tenant-head finalization throws.
    /// </summary>
    internal static string[] ValidateStoredSwitchTenants(
        InstallationIdentityCoordinatorRecord row)
    {
        var payload = ValidateStoredSwitch(row);
        return new[]
        {
            payload.OldTenantId,
            payload.Target.Membership.TenantId,
        }.Order(StringComparer.Ordinal).ToArray();
    }

    internal static SwitchPayload ValidateStoredSwitch(
        InstallationIdentityCoordinatorRecord row)
    {
        if (row.CommandType != CommandType || row.PayloadSchemaVersion != PayloadSchemaVersion)
        {
            throw new InvalidOperationException(
                "identity.session_switch_payload_invalid: command type is not admitted.");
        }
        var payload = JsonSerializer.Deserialize<SwitchPayload>(row.IntentPayloadJson, Json)
            ?? throw new InvalidOperationException(
                "identity.session_switch_payload_invalid: switch payload is missing.");
        var tenants = JsonSerializer.Deserialize<string[]>(row.TenantIdsJson, Json) ?? [];
        var expectedTenants = new[]
        {
            payload.OldTenantId,
            payload.Target.Membership.TenantId,
        }.Order(StringComparer.Ordinal).ToArray();
        var expectedPayloadDigest = ComputePayloadDigest(row.AccountId, payload);
        var expectedCorrelationId = CorrelationIdFor(
            payload.OldSessionCorrelationId,
            payload.Target.Membership.TenantId);
        var expectedFingerprint = InstallationAuditIntegrity.Hash(
            CommandType,
            expectedCorrelationId,
            expectedPayloadDigest);
        if (row.CorrelationId != expectedCorrelationId ||
            row.AuthorityEvidenceDigest != expectedPayloadDigest ||
            row.CommandFingerprint != expectedFingerprint ||
            row.ExpectedAccountSecurityVersion != payload.AccountSecurityVersion ||
            payload.OldTenantId == payload.Target.Membership.TenantId ||
            !tenants.SequenceEqual(expectedTenants, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.session_switch_payload_invalid: durable evidence was changed.");
        }
        return payload;
    }

    private async Task<SwitchTargetAuthority?> ResolveTargetAuthorityAsync(
        WebUserSessionRecord oldSession,
        string targetTenantId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var candidates = await _candidateLocator.ListForAccountAsync(
                new PrincipalUserId(oldSession.AccountId),
                correlationId,
                cancellationToken)
            .ConfigureAwait(false);
        var candidate = candidates.SingleOrDefault(item =>
            item.TenantId.Value == targetTenantId);
        if (candidate is null)
        {
            return null;
        }
        var membership = await _membershipAuthority.ResolveUsableMembershipAsync(
                oldSession.AccountId,
                targetTenantId,
                correlationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return null;
        }
        var tenant = new TenantId(targetTenantId);
        var principal = new PrincipalUserId(membership.CanonicalPrincipalId);
        var party = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        return party is null ||
            !party.VerifiedTenant.Equals(tenant) ||
            !party.PrincipalUserId.Equals(principal)
            ? null
            : new SwitchTargetAuthority(
                membership,
                party.PartyId.Value,
                candidate.DisplayLabel);
    }

    private async Task<bool> OldAuthorityIsCurrentAsync(
        WebUserSessionRecord oldSession,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var membership = await _membershipAuthority.ResolveUsableMembershipAsync(
                oldSession.AccountId,
                oldSession.TenantId,
                correlationId,
                cancellationToken)
            .ConfigureAwait(false);
        return membership is not null &&
            oldSession.PinnedGrantOwnerVersions.Count == 1 &&
            membership.MembershipId == oldSession.MembershipId &&
            membership.OwnerVersion == oldSession.MembershipOwnerVersion &&
            membership.CanonicalPrincipalId == oldSession.TenantPrincipalId &&
            membership.GrantId == oldSession.PinnedGrantOwnerVersions.Single().GrantId &&
            membership.GrantOwnerVersion ==
                oldSession.PinnedGrantOwnerVersions.Single().OwnerVersion &&
            membership.AuthorizationEpoch == oldSession.AuthorizationEpoch;
    }

    private async Task<bool> RequireCurrentActiveSessionAsync(
        WebUserSessionRecord session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var account = await identity.Accounts.AsNoTracking().SingleOrDefaultAsync(row =>
                row.AccountId == session.AccountId &&
                row.Status == InstallationAccountStatus.Active &&
                row.SecurityVersion == session.AccountSecurityVersion,
            cancellationToken).ConfigureAwait(false);
        return account is not null && await _sessions.FindActiveAsync(
                session.HandleDigest,
                account.SecurityVersion,
                now,
                cancellationToken)
            .ConfigureAwait(false) is not null;
    }

    private async Task<InstallationIdentityCoordinatorRecord?> FindHomeAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await identity.Coordinators.AsNoTracking().SingleOrDefaultAsync(
            row => row.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallationIdentityCoordinatorRecord> CreateHomeAsync(
        string correlationId,
        WebUserSessionRecord oldSession,
        SwitchPayload payload,
        string payloadDigest,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await identity.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var existing = await identity.Coordinators.AsNoTracking().SingleOrDefaultAsync(
            row => row.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }
        var account = await identity.Accounts.AsNoTracking().SingleOrDefaultAsync(row =>
                row.AccountId == oldSession.AccountId &&
                row.Status == InstallationAccountStatus.Active &&
                row.SecurityVersion == oldSession.AccountSecurityVersion,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "identity.session_switch_account_stale: account authority changed before prepare.");
        var tenantIds = new[]
        {
            payload.OldTenantId,
            payload.Target.Membership.TenantId,
        }.Order(StringComparer.Ordinal).ToArray();
        var now = _timeProvider.GetUtcNow();
        var row = new InstallationIdentityCoordinatorRecord
        {
            CorrelationId = correlationId,
            CommandType = CommandType,
            CommandFingerprint = fingerprint,
            PayloadSchemaVersion = PayloadSchemaVersion,
            AccountId = account.AccountId,
            ActorAccountId = account.AccountId,
            AuthorityEvidenceDigest = payloadDigest,
            ExpectedAccountOwnerVersion = account.OwnerVersion,
            ExpectedAccountSecurityVersion = account.SecurityVersion,
            ExpectedActorOwnerVersion = account.OwnerVersion,
            ExpectedActorSecurityVersion = account.SecurityVersion,
            TenantIdsJson = JsonSerializer.Serialize(tenantIds, Json),
            IntentPayloadJson = JsonSerializer.Serialize(payload, Json),
            FinalReceiptsJson = "[]",
            State = InstallationIdentityCoordinatorState.Preparing,
            FailureCode = null,
            OwnerVersion = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        identity.Coordinators.Add(row);
        try
        {
            await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            identity.ChangeTracker.Clear();
            return await identity.Coordinators.AsNoTracking().SingleAsync(
                item => item.CorrelationId == correlationId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SwitchPartitions> ResolvePartitionsAsync(
        SwitchPayload payload,
        CancellationToken cancellationToken)
    {
        var oldPartition = await _partitions.ResolveAsync(
                payload.OldTenantId,
                cancellationToken)
            .ConfigureAwait(false);
        var targetPartition = await _partitions.ResolveAsync(
                payload.Target.Membership.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        return new SwitchPartitions(oldPartition, targetPartition);
    }

    private static async Task<IReadOnlyList<HeldLease>?> AcquireLeasesAsync(
        SwitchPartitions partitions,
        CancellationToken cancellationToken)
    {
        var ordered = new[] { partitions.Old, partitions.Target }
            .OrderBy(item => item.TenantId, StringComparer.Ordinal)
            .ToArray();
        var held = new List<HeldLease>(ordered.Length);
        foreach (var partition in ordered)
        {
            var lease = await partition.Leases.AcquireAsync(
                    $"identity.membership:{partition.TenantId}",
                    LeaseDuration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lease is null)
            {
                await ReleaseLeasesAsync(held).ConfigureAwait(false);
                return null;
            }
            held.Add(new HeldLease(partition.Leases, lease));
        }
        return held;
    }

    private static async Task ReleaseLeasesAsync(IReadOnlyList<HeldLease> held)
    {
        for (var index = held.Count - 1; index >= 0; index--)
        {
            try
            {
                await held[index].Coordinator.ReleaseAsync(
                        held[index].Lease,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Bounded leases expire fail-safe; recovery reacquires in canonical order.
            }
        }
    }

    private async Task AbortPreparingAsync(
        InstallationIdentityCoordinatorRecord home,
        SwitchPartitions partitions,
        CancellationToken cancellationToken)
    {
        await using (var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var row = await identity.Coordinators.SingleAsync(
                item => item.CorrelationId == home.CorrelationId,
                cancellationToken).ConfigureAwait(false);
            if (row.State == InstallationIdentityCoordinatorState.Preparing)
            {
                row.State = InstallationIdentityCoordinatorState.Aborted;
                row.FailureCode = "identity.session_switch_aborted";
                row.OwnerVersion++;
                row.UpdatedAtUtc = _timeProvider.GetUtcNow();
                await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await partitions.Old.Memberships.AbortSessionRevocationAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        await partitions.Target.Memberships.AbortSessionSelectionAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InstallationIdentityCoordinatorRecord> AdvanceHomeAsync(
        string correlationId,
        InstallationIdentityCoordinatorState expected,
        InstallationIdentityCoordinatorState next,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await identity.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State == next)
        {
            return row;
        }
        if (row.State != expected)
        {
            throw new InvalidOperationException(
                $"identity.session_switch_state_invalid: expected {expected}, observed {row.State}.");
        }
        row.State = next;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<SwitchReceipts> FinalizeTenantHeadsAsync(
        SwitchPartitions partitions,
        InstallationIdentityCoordinatorRecord home,
        SwitchPayload payload,
        CancellationToken cancellationToken)
    {
        TenantSessionRevocationReceipt? revocation = null;
        TenantSessionSelectionReceipt? selection = null;
        foreach (var tenantId in new[]
                 {
                     payload.OldTenantId,
                     payload.Target.Membership.TenantId,
                 }.Order(StringComparer.Ordinal))
        {
            if (tenantId == payload.OldTenantId)
            {
                revocation = await partitions.Old.Memberships.FinalizeSessionRevocationAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateRevocationReceipt(home, payload, revocation);
            }
            else
            {
                selection = await partitions.Target.Memberships.FinalizeSessionSelectionAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateSelectionReceipt(home, payload, selection);
            }
        }
        return new SwitchReceipts(
            revocation ?? throw new InvalidOperationException(
                "identity.session_switch_receipt_invalid: old-tenant receipt is missing."),
            selection ?? throw new InvalidOperationException(
                "identity.session_switch_receipt_invalid: target-tenant receipt is missing."));
    }

    private async Task<InstallationIdentityCoordinatorRecord> PersistReceiptsAndFinalizeAsync(
        string correlationId,
        SwitchReceipts receipts,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await identity.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State != InstallationIdentityCoordinatorState.Committing)
        {
            throw new InvalidOperationException(
                "identity.session_switch_state_invalid: receipts require Committing.");
        }
        row.FinalReceiptsJson = JsonSerializer.Serialize(receipts, Json);
        row.State = InstallationIdentityCoordinatorState.Finalizing;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task RequireDurableTenantReceiptsAsync(
        SwitchPartitions partitions,
        InstallationIdentityCoordinatorRecord home,
        SwitchPayload payload,
        SwitchReceipts expected,
        CancellationToken cancellationToken)
    {
        var revocation = await partitions.Old.Memberships.FinalizeSessionRevocationAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        var selection = await partitions.Target.Memberships.FinalizeSessionSelectionAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (revocation != expected.Revocation || selection != expected.Selection)
        {
            throw new InvalidOperationException(
                "identity.session_switch_receipt_mismatch: tenant receipt evidence changed.");
        }
        ValidateRevocationReceipt(home, payload, revocation);
        ValidateSelectionReceipt(home, payload, selection);
    }

    private static void ValidateRevocationReceipt(
        InstallationIdentityCoordinatorRecord home,
        SwitchPayload payload,
        TenantSessionRevocationReceipt receipt)
    {
        var expectedIntentDigest = InstallationAuditIntegrity.Hash(
            home.CorrelationId,
            home.CommandFingerprint,
            home.AccountId,
            payload.OldMembershipId,
            payload.OldSessionCorrelationId,
            home.AuthorityEvidenceDigest);
        if (receipt.TenantId != payload.OldTenantId ||
            receipt.MembershipId != payload.OldMembershipId ||
            receipt.SessionCorrelationId != payload.OldSessionCorrelationId ||
            receipt.DocumentOwnerVersion <= 0 ||
            receipt.AuditSequence <= 0 ||
            receipt.AuditHeadHash is not { Length: 64 } ||
            receipt.IntentDigest != expectedIntentDigest ||
            receipt.HomeDecisionDigest is not { Length: 64 })
        {
            throw new InvalidOperationException(
                "identity.session_switch_receipt_invalid: old-tenant evidence is not authoritative.");
        }
    }

    private static void ValidateSelectionReceipt(
        InstallationIdentityCoordinatorRecord home,
        SwitchPayload payload,
        TenantSessionSelectionReceipt receipt)
    {
        var expectedIntentDigest = InstallationAuditIntegrity.Hash(
            home.CorrelationId,
            home.CommandFingerprint,
            home.AccountId,
            payload.Target.Membership.MembershipId,
            home.AuthorityEvidenceDigest);
        if (receipt.TenantId != payload.Target.Membership.TenantId ||
            receipt.MembershipId != payload.Target.Membership.MembershipId ||
            receipt.DocumentOwnerVersion <= 0 ||
            receipt.AuditSequence <= 0 ||
            receipt.AuditHeadHash is not { Length: 64 } ||
            receipt.IntentDigest != expectedIntentDigest ||
            receipt.HomeDecisionDigest is not { Length: 64 })
        {
            throw new InvalidOperationException(
                "identity.session_switch_receipt_invalid: target-tenant evidence is not authoritative.");
        }
    }

    private async Task<InstallationIdentityCoordinatorRecord> CompleteWithInstallationAuditAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await identity.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await identity.Coordinators.SingleAsync(
            item => item.CorrelationId == correlationId,
            cancellationToken).ConfigureAwait(false);
        if (row.State == InstallationIdentityCoordinatorState.Completed)
        {
            return row;
        }
        if (row.State != InstallationIdentityCoordinatorState.Finalizing)
        {
            throw new InvalidOperationException(
                "identity.session_switch_state_invalid: expected Finalizing.");
        }
        _ = DeserializeReceipts(row.FinalReceiptsJson);

        var installation = await identity.InstallationIdentities.SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var root = await identity.RootKeyEpochs.SingleAsync(item =>
                item.InstallationIdentityId == installation.InstallationIdentityId &&
                item.EpochNumber == installation.ActiveRootEpoch,
            cancellationToken).ConfigureAwait(false);
        var head = await identity.AuditHeads.SingleAsync(item =>
                item.InstallationIdentityId == installation.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        var chain = await identity.AuditEnvelopes.AsNoTracking()
            .Where(item => item.InstallationIdentityId == installation.InstallationIdentityId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (root.Status != InstallationRootEpochStatus.Active ||
            !InstallationAuditIntegrity.HasValidChain(chain, head, installation.InstallationIdentityId))
        {
            throw new InvalidOperationException(
                "identity.session_switch_audit_invalid: installation audit is not authoritative.");
        }

        var now = _timeProvider.GetUtcNow();
        var envelope = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = installation.InstallationIdentityId,
            Sequence = head.Sequence + 1,
            CorrelationId = row.CorrelationId,
            CommandFingerprint = row.CommandFingerprint,
            EventType = "WebTenantSwitchCompleted",
            ActorKind = "installation-account",
            ActorId = row.AccountId,
            RootEpoch = root.EpochNumber,
            RootPublicKeyFingerprint = root.RootPublicKeyFingerprint,
            PreviousHash = head.HeadHash,
            EnvelopeHash = string.Empty,
            PayloadDigest = InstallationAuditIntegrity.Hash(
                row.AccountId,
                row.AuthorityEvidenceDigest,
                row.FinalReceiptsJson),
            OccurredAtUtc = now,
        };
        envelope.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(envelope);
        identity.AuditEnvelopes.Add(envelope);
        head.Sequence = envelope.Sequence;
        head.HeadHash = envelope.EnvelopeHash;
        head.OwnerVersion++;
        head.UpdatedAtUtc = now;
        row.State = InstallationIdentityCoordinatorState.Completed;
        row.FailureCode = null;
        row.OwnerVersion++;
        row.UpdatedAtUtc = now;
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<WebTenantSelectionResult?> RotateSessionsAsync(
        WebUserSessionRecord oldSession,
        SwitchPayload payload,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (now < oldSession.IssuedAtUtc || now >= oldSession.AbsoluteExpiresAtUtc)
        {
            return null;
        }
        await using (var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!await identity.Accounts.AsNoTracking().AnyAsync(row =>
                    row.AccountId == oldSession.AccountId &&
                    row.Status == InstallationAccountStatus.Active &&
                    row.SecurityVersion == oldSession.AccountSecurityVersion,
                cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        await using var sessions = await _sessionFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await sessions.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        if (await sessions.UserSessions.AsNoTracking().AnyAsync(
                row => row.CoordinationCorrelationId == correlationId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var durableOld = await _sessions.FindStoredAsync(
                sessions,
                oldSession.HandleDigest,
                track: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (durableOld is null ||
            !string.Equals(
                durableOld.SessionCorrelationId,
                oldSession.SessionCorrelationId,
                StringComparison.Ordinal) ||
            !HasSameAuthorityCoordinates(oldSession, durableOld) ||
            await sessions.Revocations.AsNoTracking().AnyAsync(row =>
                    row.Audience == WebCookieAudience.SelectedSession &&
                    row.SubjectCorrelationId == oldSession.SessionCorrelationId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var newHandle = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var newSessionCorrelationId = RandomHex(32);
        var antiforgery = WebAntiforgeryStateStore.CreateState(
            WebCookieAudience.SelectedSession,
            oldSession.AccountId,
            newSessionCorrelationId,
            correlationId,
            now,
            oldSession.AbsoluteExpiresAtUtc);
        var idleExpires = now + _sessionOptions.IdleTimeout;
        if (idleExpires > oldSession.AbsoluteExpiresAtUtc)
        {
            idleExpires = oldSession.AbsoluteExpiresAtUtc;
        }
        sessions.UserSessions.Add(new WebUserSessionRecord(
            SessionCorrelationId: newSessionCorrelationId,
            AccountId: oldSession.AccountId,
            AccountSecurityVersion: oldSession.AccountSecurityVersion,
            TenantId: payload.Target.Membership.TenantId,
            MembershipId: payload.Target.Membership.MembershipId,
            MembershipOwnerVersion: payload.Target.Membership.OwnerVersion,
            TenantPrincipalId: payload.Target.Membership.CanonicalPrincipalId,
            CanonicalPartyReference: payload.Target.CanonicalPartyReference,
            PinnedGrantOwnerVersions:
                [new PinnedGrantOwnerVersion(
                    payload.Target.Membership.GrantId,
                    payload.Target.Membership.GrantOwnerVersion)],
            AuthorizationEpoch: payload.Target.Membership.AuthorizationEpoch,
            HandleDigest: Digest(newHandle),
            AntiforgeryStateId: antiforgery.State.AntiforgeryStateId,
            CoordinationCorrelationId: correlationId,
            IssuedAtUtc: now,
            IdleExpiresAtUtc: idleExpires,
            AbsoluteExpiresAtUtc: oldSession.AbsoluteExpiresAtUtc,
            OwnerVersion: 1));
        sessions.AntiforgeryStates.Add(antiforgery.State);
        sessions.Revocations.Add(new WebSessionRevocationRecord(
            RevocationId: Guid.NewGuid().ToString("N"),
            Audience: WebCookieAudience.SelectedSession,
            AccountId: oldSession.AccountId,
            SubjectCorrelationId: oldSession.SessionCorrelationId,
            SupersededByCorrelationId: newSessionCorrelationId,
            ReasonCode: ReasonCode,
            CoordinationCorrelationId: correlationId,
            RevokedAtUtc: now,
            OwnerVersion: 1));
        await sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WebTenantSelectionResult(
            newHandle,
            antiforgery.Token,
            payload.Target.Membership.TenantId,
            payload.Target.DisplayName,
            oldSession.AbsoluteExpiresAtUtc);
    }

    private static bool HasSameAuthorityCoordinates(
        WebUserSessionRecord expected,
        WebUserSessionRecord actual) =>
        expected.AccountId == actual.AccountId &&
        expected.AccountSecurityVersion == actual.AccountSecurityVersion &&
        expected.TenantId == actual.TenantId &&
        expected.MembershipId == actual.MembershipId &&
        expected.MembershipOwnerVersion == actual.MembershipOwnerVersion &&
        expected.TenantPrincipalId == actual.TenantPrincipalId &&
        expected.CanonicalPartyReference == actual.CanonicalPartyReference &&
        expected.PinnedGrantOwnerVersions.SequenceEqual(actual.PinnedGrantOwnerVersions) &&
        expected.AuthorizationEpoch == actual.AuthorizationEpoch &&
        expected.AbsoluteExpiresAtUtc == actual.AbsoluteExpiresAtUtc;

    private static SwitchReceipts DeserializeReceipts(string json) =>
        JsonSerializer.Deserialize<SwitchReceipts>(json, Json)
        ?? throw new InvalidOperationException(
            "identity.session_switch_receipt_invalid: two tenant receipts are required.");

    private static string ComputePayloadDigest(string accountId, SwitchPayload payload) =>
        InstallationAuditIntegrity.Hash(
            accountId,
            payload.OldTenantId,
            payload.OldMembershipId,
            payload.OldSessionCorrelationId,
            payload.OldHandleDigest,
            payload.AccountSecurityVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            payload.Target.Membership.TenantId,
            payload.Target.Membership.MembershipId,
            payload.Target.Membership.OwnerVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            payload.Target.Membership.CanonicalPrincipalId,
            payload.Target.CanonicalPartyReference,
            payload.Target.DisplayName,
            payload.Target.Membership.GrantId,
            payload.Target.Membership.GrantOwnerVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            payload.Target.Membership.AuthorizationEpoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static string CorrelationIdFor(
        string oldSessionCorrelationId,
        string targetTenantId) =>
        InstallationAuditIntegrity.Hash(
            CommandType,
            oldSessionCorrelationId,
            targetTenantId);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RandomHex(int byteLength) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength)).ToLowerInvariant();

    internal sealed record SwitchTargetAuthority(
        TenantMembershipSnapshot Membership,
        string CanonicalPartyReference,
        string DisplayName);

    internal sealed record SwitchPayload(
        string OldTenantId,
        string OldMembershipId,
        string OldSessionCorrelationId,
        string OldHandleDigest,
        long AccountSecurityVersion,
        SwitchTargetAuthority Target);

    private sealed record SwitchReceipts(
        TenantSessionRevocationReceipt Revocation,
        TenantSessionSelectionReceipt Selection);

    private sealed record SwitchPartitions(
        TenantIdentityAuthorityPartition Old,
        TenantIdentityAuthorityPartition Target);

    private sealed record HeldLease(
        ILeaseCoordinator Coordinator,
        Lease Lease);
}
