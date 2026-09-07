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

/// <summary>Committed tenant-selection result; the handle is emitted only as the selected cookie.</summary>
public sealed record WebTenantSelectionResult(
    string Handle,
    string AntiforgeryToken,
    string TenantId,
    string DisplayName,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Consumes an account challenge to establish one tenant-bound web session.</summary>
public interface IWebTenantSelectionAuthority
{
    Task<WebTenantSelectionResult?> SelectAsync(
        string? challengeHandle,
        string? requestedTenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Challenge-only tenant selection. The challenge nonce is the replay key for one durable R3-H
/// transition; a selected handle is returned only after tenant and installation audit heads complete.
/// </summary>
internal sealed class WebTenantSelectionAuthority : IWebTenantSelectionAuthority
{
    internal const string CommandType = "WebTenantSelection";
    internal const int PayloadSchemaVersion = 1;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _sessionFactory;
    private readonly IInstallationTenantCandidateLocator _candidateLocator;
    private readonly InstallationIdentityCoordinatorService _membershipAuthority;
    private readonly ITenantIdentityAuthorityPartitionResolver _partitions;
    private readonly ICanonicalPrincipalPartyReader _partyReader;
    private readonly IInstallationIdentityV1AuthorityGate _v1AuthorityGate;
    private readonly SessionOptions _sessionOptions;
    private readonly TimeProvider _timeProvider;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public WebTenantSelectionAuthority(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
        IInstallationTenantCandidateLocator candidateLocator,
        InstallationIdentityCoordinatorService membershipAuthority,
        ITenantIdentityAuthorityPartitionResolver partitions,
        ICanonicalPrincipalPartyReader partyReader,
        IInstallationIdentityV1AuthorityGate v1AuthorityGate,
        IOptions<SessionOptions> sessionOptions,
        TimeProvider timeProvider)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _candidateLocator = candidateLocator ?? throw new ArgumentNullException(nameof(candidateLocator));
        _membershipAuthority = membershipAuthority
            ?? throw new ArgumentNullException(nameof(membershipAuthority));
        _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        _partyReader = partyReader ?? throw new ArgumentNullException(nameof(partyReader));
        _v1AuthorityGate = v1AuthorityGate ?? throw new ArgumentNullException(nameof(v1AuthorityGate));
        _sessionOptions = sessionOptions?.Value
            ?? throw new ArgumentNullException(nameof(sessionOptions));
        _sessionOptions.Validate();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WebTenantSelectionResult?> SelectAsync(
        string? challengeHandle,
        string? requestedTenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(challengeHandle))
        {
            return null;
        }

        // The cutover gate, consulted only once a handle is actually presented (same ordering as the
        // legacy web-session entry points, so a handle-less call never reaches the cutover store).
        //
        // Consuming an account challenge IS an accept path for the AccountChallenge legacy bearer
        // audience: the handle was minted against pre-migration rows, so ADR 0160 R3-H step 6 revokes
        // that audience before the marker CAS and the listener "rejects every v1 cookie or invitation
        // audience after the marker flip even if a legacy source row remains physically present".
        // Without this check the marker could commit while an already-issued challenge stayed
        // spendable here, minting a fresh selected session from retired v1 authority.
        var admission = await _v1AuthorityGate
            .CheckLegacyBearerAdmissionAsync(
                InstallationIdentityLegacyBearerAudience.AccountChallenge,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            return null;
        }

        var challengeDigest = Digest(challengeHandle);
        WebAccountAccessChallengeRecord? challenge;
        await using (var sessions = await _sessionFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            challenge = await sessions.AccountAccessChallenges.AsNoTracking()
                .SingleOrDefaultAsync(row => row.HandleDigest == challengeDigest, cancellationToken)
                .ConfigureAwait(false);
        }
        var now = _timeProvider.GetUtcNow();
        if (challenge is null || challenge.ConsumedAtUtc is not null || challenge.RevokedAtUtc is not null ||
            challenge.IsExpired(now))
        {
            return null;
        }

        var authority = await ResolveAuthorityAsync(challenge, requestedTenantId, cancellationToken)
            .ConfigureAwait(false);
        if (authority is null)
        {
            return null;
        }

        var payloadDigest = ComputePayloadDigest(challenge, authority);
        var fingerprint = InstallationAuditIntegrity.Hash(
            CommandType,
            challenge.CoordinationCorrelationId,
            payloadDigest);
        var home = await GetOrCreateHomeAsync(
                challenge,
                authority,
                payloadDigest,
                fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(home.CommandFingerprint, fingerprint, StringComparison.Ordinal) ||
            home.State == InstallationIdentityCoordinatorState.Aborted)
        {
            return null;
        }

        var partition = await _partitions.ResolveAsync(authority.Membership.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var lease = await partition.Leases.AcquireAsync(
                $"identity.membership:{authority.Membership.TenantId}",
                LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return null;
        }

        try
        {
            if (home.State == InstallationIdentityCoordinatorState.Completed)
            {
                var completedReceipt = DeserializeReceipt(home.FinalReceiptsJson);
                await RequireDurableTenantReceiptAsync(
                        partition,
                        home,
                        completedReceipt,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await MintSelectedSessionAsync(
                        challenge,
                        authority,
                        home.CorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (home.State == InstallationIdentityCoordinatorState.Preparing)
            {
                await partition.Memberships.PrepareSessionSelectionAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        home.AccountId,
                        authority.Membership.MembershipId,
                        payloadDigest,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Reject stale authority at the effect site, after preparation and immediately before the
                // durable commit decision. This includes the challenge's pinned security version.
                var current = await ResolveAuthorityAsync(challenge, authority.Membership.TenantId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (current is null || !Equals(current, authority))
                {
                    await AbortPreparingAsync(home, partition, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                home = await AdvanceHomeAsync(
                        home.CorrelationId,
                        InstallationIdentityCoordinatorState.Preparing,
                        InstallationIdentityCoordinatorState.Committing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            TenantSessionSelectionReceipt receipt;
            if (home.State == InstallationIdentityCoordinatorState.Committing)
            {
                receipt = await partition.Memberships.FinalizeSessionSelectionAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateTenantReceipt(home, receipt);
                home = await PersistReceiptAndFinalizeAsync(home.CorrelationId, receipt, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                receipt = DeserializeReceipt(home.FinalReceiptsJson);
            }

            if (home.State != InstallationIdentityCoordinatorState.Finalizing ||
                !string.Equals(receipt.TenantId, authority.Membership.TenantId, StringComparison.Ordinal))
            {
                return null;
            }

            await RequireDurableTenantReceiptAsync(partition, home, receipt, cancellationToken)
                .ConfigureAwait(false);

            home = await CompleteWithInstallationAuditAsync(home.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
            if (home.State != InstallationIdentityCoordinatorState.Completed)
            {
                return null;
            }

            // The selected-session row and challenge consumption are deliberately last. A crash
            // before this point leaves the challenge retryable by correlation; no destination
            // audience exists until both owning audit heads have reached Completed.
            return await MintSelectedSessionAsync(
                    challenge,
                    authority,
                    home.CorrelationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await partition.Leases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The bounded lease expires fail-safe; recovery must reacquire before another write.
            }
        }
    }

    private async Task RequireDurableTenantReceiptAsync(
        TenantIdentityAuthorityPartition partition,
        InstallationIdentityCoordinatorRecord home,
        TenantSessionSelectionReceipt expected,
        CancellationToken cancellationToken)
    {
        var durable = await partition.Memberships.FinalizeSessionSelectionAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (durable != expected)
        {
            throw new InvalidOperationException(
                "identity.session_selection_receipt_mismatch: tenant receipt evidence changed.");
        }
        ValidateTenantReceipt(home, durable);
    }

    private static void ValidateTenantReceipt(
        InstallationIdentityCoordinatorRecord home,
        TenantSessionSelectionReceipt receipt)
    {
        var authority = JsonSerializer.Deserialize<SelectionAuthority>(home.IntentPayloadJson, Json)
            ?? throw new InvalidOperationException(
                "identity.session_selection_receipt_invalid: selection authority is missing.");
        var expectedIntentDigest = InstallationAuditIntegrity.Hash(
            home.CorrelationId,
            home.CommandFingerprint,
            home.AccountId,
            authority.Membership.MembershipId,
            home.AuthorityEvidenceDigest);
        if (receipt.TenantId != authority.Membership.TenantId ||
            receipt.MembershipId != authority.Membership.MembershipId ||
            receipt.DocumentOwnerVersion <= 0 ||
            receipt.AuditSequence <= 0 ||
            receipt.AuditHeadHash is not { Length: 64 } ||
            receipt.IntentDigest != expectedIntentDigest ||
            receipt.HomeDecisionDigest is not { Length: 64 })
        {
            throw new InvalidOperationException(
                "identity.session_selection_receipt_invalid: tenant evidence is not authoritative.");
        }
    }

    internal static string[] ValidateStoredSelection(InstallationIdentityCoordinatorRecord row)
    {
        if (row.CommandType != CommandType || row.PayloadSchemaVersion != PayloadSchemaVersion)
        {
            throw new InvalidOperationException(
                "identity.session_selection_payload_invalid: command type is not admitted.");
        }
        var payload = JsonSerializer.Deserialize<SelectionAuthority>(row.IntentPayloadJson, Json)
            ?? throw new InvalidOperationException(
                "identity.session_selection_payload_invalid: authority payload is missing.");
        var tenants = JsonSerializer.Deserialize<string[]>(row.TenantIdsJson, Json) ?? [];
        var expectedPayloadDigest = ComputePayloadDigest(row.AccountId, row.ExpectedAccountSecurityVersion,
            payload);
        var expectedFingerprint = InstallationAuditIntegrity.Hash(
            CommandType,
            row.CorrelationId,
            expectedPayloadDigest);
        if (row.AuthorityEvidenceDigest != expectedPayloadDigest ||
            row.CommandFingerprint != expectedFingerprint ||
            tenants.Length != 1 || tenants[0] != payload.Membership.TenantId)
        {
            throw new InvalidOperationException(
                "identity.session_selection_payload_invalid: durable evidence was changed.");
        }
        return tenants;
    }

    private async Task<SelectionAuthority?> ResolveAuthorityAsync(
        WebAccountAccessChallengeRecord challenge,
        string? requestedTenantId,
        CancellationToken cancellationToken)
    {
        await using (var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var current = await identity.Accounts.AsNoTracking().SingleOrDefaultAsync(row =>
                    row.AccountId == challenge.AccountId &&
                    row.Status == InstallationAccountStatus.Active &&
                    row.SecurityVersion == challenge.AccountSecurityVersion,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }
        }

        var candidates = await _candidateLocator.ListForAccountAsync(
                new PrincipalUserId(challenge.AccountId),
                challenge.CoordinationCorrelationId,
                cancellationToken)
            .ConfigureAwait(false);
        string tenantId;
        if (requestedTenantId is null)
        {
            if (candidates.Count != 1)
            {
                return null;
            }
            tenantId = candidates[0].TenantId.Value;
        }
        else if (!Guid.TryParse(requestedTenantId, out var parsedTenant))
        {
            return null;
        }
        else
        {
            tenantId = parsedTenant.ToString("D");
            if (!candidates.Any(item => item.TenantId.Value == tenantId))
            {
                return null;
            }
        }

        var membership = await _membershipAuthority.ResolveUsableMembershipAsync(
                challenge.AccountId,
                tenantId,
                challenge.CoordinationCorrelationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return null;
        }

        var tenant = new TenantId(tenantId);
        var principal = new PrincipalUserId(membership.CanonicalPrincipalId);
        var party = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (party is null)
        {
            return null;
        }
        var label = candidates.Single(item => item.TenantId.Value == tenantId).DisplayLabel;
        return new SelectionAuthority(membership, party.PartyId.Value, label);
    }

    private async Task<InstallationIdentityCoordinatorRecord> GetOrCreateHomeAsync(
        WebAccountAccessChallengeRecord challenge,
        SelectionAuthority authority,
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
            row => row.CorrelationId == challenge.CoordinationCorrelationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateStoredSelection(existing);
            return existing;
        }

        var account = await identity.Accounts.AsNoTracking().SingleAsync(
            row => row.AccountId == challenge.AccountId,
            cancellationToken).ConfigureAwait(false);
        var row = new InstallationIdentityCoordinatorRecord
        {
            CorrelationId = challenge.CoordinationCorrelationId,
            CommandType = CommandType,
            CommandFingerprint = fingerprint,
            PayloadSchemaVersion = PayloadSchemaVersion,
            AccountId = account.AccountId,
            ActorAccountId = account.AccountId,
            AuthorityEvidenceDigest = payloadDigest,
            ExpectedAccountOwnerVersion = account.OwnerVersion,
            ExpectedAccountSecurityVersion = challenge.AccountSecurityVersion,
            ExpectedActorOwnerVersion = account.OwnerVersion,
            ExpectedActorSecurityVersion = challenge.AccountSecurityVersion,
            TenantIdsJson = JsonSerializer.Serialize(new[] { authority.Membership.TenantId }, Json),
            IntentPayloadJson = JsonSerializer.Serialize(authority, Json),
            FinalReceiptsJson = "[]",
            State = InstallationIdentityCoordinatorState.Preparing,
            FailureCode = null,
            OwnerVersion = 1,
            CreatedAtUtc = _timeProvider.GetUtcNow(),
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
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
            var durable = await identity.Coordinators.AsNoTracking().SingleAsync(
                item => item.CorrelationId == challenge.CoordinationCorrelationId,
                cancellationToken).ConfigureAwait(false);
            ValidateStoredSelection(durable);
            return durable;
        }
    }

    private async Task AbortPreparingAsync(
        InstallationIdentityCoordinatorRecord home,
        TenantIdentityAuthorityPartition partition,
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
                row.FailureCode = "identity.session_selection_aborted";
                row.OwnerVersion++;
                row.UpdatedAtUtc = _timeProvider.GetUtcNow();
                await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await partition.Memberships.AbortSessionSelectionAsync(
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
                $"identity.session_selection_state_invalid: expected {expected}, observed {row.State}.");
        }
        row.State = next;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<InstallationIdentityCoordinatorRecord> PersistReceiptAndFinalizeAsync(
        string correlationId,
        TenantSessionSelectionReceipt receipt,
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
                "identity.session_selection_state_invalid: receipt requires Committing.");
        }
        row.FinalReceiptsJson = JsonSerializer.Serialize(new[] { receipt }, Json);
        row.State = InstallationIdentityCoordinatorState.Finalizing;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<WebTenantSelectionResult?> MintSelectedSessionAsync(
        WebAccountAccessChallengeRecord source,
        SelectionAuthority authority,
        string coordinationCorrelationId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var sessions = await _sessionFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await sessions.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        if (await sessions.UserSessions.AsNoTracking().AnyAsync(
                row => row.CoordinationCorrelationId == coordinationCorrelationId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var challenge = await sessions.AccountAccessChallenges.SingleOrDefaultAsync(
            row => row.HandleDigest == source.HandleDigest,
            cancellationToken).ConfigureAwait(false);
        if (challenge is null || challenge.ConsumedAtUtc is not null || challenge.RevokedAtUtc is not null ||
            challenge.IsExpired(now))
        {
            return null;
        }

        await using (var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var current = await identity.Accounts.AsNoTracking().SingleOrDefaultAsync(row =>
                    row.AccountId == source.AccountId &&
                    row.Status == InstallationAccountStatus.Active &&
                    row.SecurityVersion == source.AccountSecurityVersion,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }
        }

        var handle = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var absoluteExpires = now + _sessionOptions.AbsoluteLifetime;
        var idleExpires = now + _sessionOptions.IdleTimeout;
        var sessionCorrelationId = RandomHex(32);
        var antiforgery = WebAntiforgeryStateStore.CreateState(
            WebCookieAudience.SelectedSession,
            source.AccountId,
            sessionCorrelationId,
            coordinationCorrelationId,
            now,
            absoluteExpires);
        var selected = new WebUserSessionRecord(
            SessionCorrelationId: sessionCorrelationId,
            AccountId: source.AccountId,
            AccountSecurityVersion: source.AccountSecurityVersion,
            TenantId: authority.Membership.TenantId,
            MembershipId: authority.Membership.MembershipId,
            MembershipOwnerVersion: authority.Membership.OwnerVersion,
            TenantPrincipalId: authority.Membership.CanonicalPrincipalId,
            CanonicalPartyReference: authority.CanonicalPartyReference,
            PinnedGrantOwnerVersions:
                [new PinnedGrantOwnerVersion(
                    authority.Membership.GrantId,
                    authority.Membership.GrantOwnerVersion)],
            AuthorizationEpoch: authority.Membership.AuthorizationEpoch,
            HandleDigest: Digest(handle),
            AntiforgeryStateId: antiforgery.State.AntiforgeryStateId,
            CoordinationCorrelationId: coordinationCorrelationId,
            IssuedAtUtc: now,
            IdleExpiresAtUtc: idleExpires,
            AbsoluteExpiresAtUtc: absoluteExpires,
            OwnerVersion: 1);
        sessions.UserSessions.Add(selected);
        sessions.AntiforgeryStates.Add(antiforgery.State);
        sessions.Entry(challenge).CurrentValues.SetValues(challenge with
        {
            ConsumedAtUtc = now,
            OwnerVersion = checked(challenge.OwnerVersion + 1),
        });
        try
        {
            await sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WebTenantSelectionResult(
                handle,
                antiforgery.Token,
                authority.Membership.TenantId,
                authority.DisplayName,
                absoluteExpires);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
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
                "identity.session_selection_state_invalid: expected Finalizing.");
        }
        _ = DeserializeReceipt(row.FinalReceiptsJson);

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
                "identity.session_selection_audit_invalid: installation audit is not authoritative.");
        }

        var now = _timeProvider.GetUtcNow();
        var envelope = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = installation.InstallationIdentityId,
            Sequence = head.Sequence + 1,
            CorrelationId = row.CorrelationId,
            CommandFingerprint = row.CommandFingerprint,
            EventType = "WebTenantSelectionCompleted",
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

    private static TenantSessionSelectionReceipt DeserializeReceipt(string json)
    {
        var receipts = JsonSerializer.Deserialize<TenantSessionSelectionReceipt[]>(json, Json) ?? [];
        return receipts.Length == 1
            ? receipts[0]
            : throw new InvalidOperationException(
                "identity.session_selection_receipt_invalid: one tenant receipt is required.");
    }

    private static string ComputePayloadDigest(
        WebAccountAccessChallengeRecord challenge,
        SelectionAuthority authority) =>
        ComputePayloadDigest(challenge.AccountId, challenge.AccountSecurityVersion, authority);

    private static string ComputePayloadDigest(
        string accountId,
        long accountSecurityVersion,
        SelectionAuthority authority) =>
        InstallationAuditIntegrity.Hash(
            accountId,
            accountSecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            authority.Membership.TenantId,
            authority.Membership.MembershipId,
            authority.Membership.OwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            authority.Membership.CanonicalPrincipalId,
            authority.CanonicalPartyReference,
            authority.DisplayName,
            authority.Membership.GrantId,
            authority.Membership.GrantOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            authority.Membership.AuthorizationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RandomHex(int byteLength) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength)).ToLowerInvariant();

    private sealed record SelectionAuthority(
        TenantMembershipSnapshot Membership,
        string CanonicalPartyReference,
        string DisplayName);
}
