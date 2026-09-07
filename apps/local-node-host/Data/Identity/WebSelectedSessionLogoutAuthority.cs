using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Server-authoritative selected-audience logout.</summary>
public interface IWebSelectedSessionLogoutAuthority
{
    Task<bool> LogoutAsync(
        string? selectedHandle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates selected-session revocation with the owning tenant and installation audit heads.
/// </summary>
internal sealed class WebSelectedSessionLogoutAuthority : IWebSelectedSessionLogoutAuthority
{
    internal const string CommandType = "WebSelectedSessionLogout";
    internal const int PayloadSchemaVersion = 1;
    internal const string ReasonCode = "user-logout";

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly WebSelectedSessionStore _sessions;
    private readonly ITenantIdentityAuthorityPartitionResolver _partitions;
    private readonly TimeProvider _timeProvider;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public WebSelectedSessionLogoutAuthority(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        WebSelectedSessionStore sessions,
        ITenantIdentityAuthorityPartitionResolver partitions,
        TimeProvider timeProvider)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<bool> LogoutAsync(
        string? selectedHandle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedHandle))
        {
            return false;
        }

        var handleDigest = Digest(selectedHandle);
        var session = await _sessions.FindStoredAsync(handleDigest, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        var correlationId = CorrelationIdFor(session.SessionCorrelationId);
        var payload = new LogoutPayload(
            session.TenantId,
            session.MembershipId,
            session.SessionCorrelationId,
            session.AccountSecurityVersion,
            ReasonCode,
            session.HandleDigest);
        var payloadDigest = ComputePayloadDigest(session);
        var fingerprint = InstallationAuditIntegrity.Hash(CommandType, correlationId, payloadDigest);
        var existingHome = await FindHomeAsync(correlationId, cancellationToken).ConfigureAwait(false);

        if (existingHome is null)
        {
            var now = _timeProvider.GetUtcNow();
            var active = await RequireCurrentActiveSessionAsync(session, now, cancellationToken)
                .ConfigureAwait(false);
            if (!active)
            {
                return false;
            }
        }

        var home = existingHome ?? await CreateHomeAsync(
                correlationId,
                session,
                payload,
                payloadDigest,
                fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateStoredLogout(home);
        if (!string.Equals(home.CommandFingerprint, fingerprint, StringComparison.Ordinal) ||
            home.State == InstallationIdentityCoordinatorState.Aborted)
        {
            return false;
        }

        var partition = await _partitions.ResolveAsync(session.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var lease = await partition.Leases.AcquireAsync(
                $"identity.membership:{session.TenantId}",
                LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return false;
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
                await RequireMatchingRevocationAsync(session, home.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (home.State == InstallationIdentityCoordinatorState.Preparing)
            {
                await partition.Memberships.PrepareSessionRevocationAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        session.AccountId,
                        session.MembershipId,
                        session.SessionCorrelationId,
                        payloadDigest,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);

                var existingRevocation = await _sessions.FindRevocationAsync(
                        session.SessionCorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existingRevocation is null && !await RequireCurrentActiveSessionAsync(
                        session,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    await AbortPreparingAsync(home, partition, cancellationToken)
                        .ConfigureAwait(false);
                    return false;
                }

                // Make the restriction durable before advancing the home decision. If the process
                // stops between these stores, the exact revocation makes the handle unusable and a
                // retry resumes the still-Preparing home by the deterministic correlation.
                await _sessions.RevokeAsync(
                        session,
                        home.CorrelationId,
                        ReasonCode,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await RequireMatchingRevocationAsync(session, home.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);
                home = await AdvanceHomeAsync(
                        home.CorrelationId,
                        InstallationIdentityCoordinatorState.Preparing,
                        InstallationIdentityCoordinatorState.Committing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (home.State == InstallationIdentityCoordinatorState.Committing)
            {
                await RequireMatchingRevocationAsync(session, home.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);

                var receipt = await partition.Memberships.FinalizeSessionRevocationAsync(
                        home.CorrelationId,
                        home.CommandFingerprint,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateTenantReceipt(home, receipt);
                home = await PersistReceiptAndFinalizeAsync(
                        home.CorrelationId,
                        receipt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (home.State != InstallationIdentityCoordinatorState.Finalizing)
            {
                return false;
            }
            var finalReceipt = DeserializeReceipt(home.FinalReceiptsJson);
            await RequireDurableTenantReceiptAsync(partition, home, finalReceipt, cancellationToken)
                .ConfigureAwait(false);
            await RequireMatchingRevocationAsync(session, home.CorrelationId, cancellationToken)
                .ConfigureAwait(false);

            home = await CompleteWithInstallationAuditAsync(home.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
            return home.State == InstallationIdentityCoordinatorState.Completed;
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

    internal static string[] ValidateStoredLogout(InstallationIdentityCoordinatorRecord row)
    {
        if (row.CommandType != CommandType || row.PayloadSchemaVersion != PayloadSchemaVersion)
        {
            throw new InvalidOperationException(
                "identity.session_logout_payload_invalid: command type is not admitted.");
        }
        var payload = JsonSerializer.Deserialize<LogoutPayload>(row.IntentPayloadJson, Json)
            ?? throw new InvalidOperationException(
                "identity.session_logout_payload_invalid: logout payload is missing.");
        var tenants = JsonSerializer.Deserialize<string[]>(row.TenantIdsJson, Json) ?? [];
        var expectedPayloadDigest = ComputePayloadDigest(row.AccountId, payload);
        var expectedCorrelationId = CorrelationIdFor(payload.SessionCorrelationId);
        var expectedFingerprint = InstallationAuditIntegrity.Hash(
            CommandType,
            expectedCorrelationId,
            expectedPayloadDigest);
        if (row.CorrelationId != expectedCorrelationId ||
            row.AuthorityEvidenceDigest != expectedPayloadDigest ||
            row.CommandFingerprint != expectedFingerprint ||
            row.ExpectedAccountSecurityVersion != payload.AccountSecurityVersion ||
            tenants.Length != 1 || tenants[0] != payload.TenantId ||
            payload.ReasonCode != ReasonCode)
        {
            throw new InvalidOperationException(
                "identity.session_logout_payload_invalid: durable evidence was changed.");
        }
        return tenants;
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
        WebUserSessionRecord session,
        LogoutPayload payload,
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
                row.AccountId == session.AccountId &&
                row.Status == InstallationAccountStatus.Active &&
                row.SecurityVersion == session.AccountSecurityVersion,
            cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            throw new InvalidOperationException(
                "identity.session_logout_account_stale: account authority changed before prepare.");
        }

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
            TenantIdsJson = JsonSerializer.Serialize(new[] { session.TenantId }, Json),
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
                row.FailureCode = "identity.session_logout_aborted";
                row.OwnerVersion++;
                row.UpdatedAtUtc = _timeProvider.GetUtcNow();
                await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await partition.Memberships.AbortSessionRevocationAsync(
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
                $"identity.session_logout_state_invalid: expected {expected}, observed {row.State}.");
        }
        row.State = next;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<InstallationIdentityCoordinatorRecord> PersistReceiptAndFinalizeAsync(
        string correlationId,
        TenantSessionRevocationReceipt receipt,
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
                "identity.session_logout_state_invalid: receipt requires Committing.");
        }
        row.FinalReceiptsJson = JsonSerializer.Serialize(new[] { receipt }, Json);
        row.State = InstallationIdentityCoordinatorState.Finalizing;
        row.OwnerVersion++;
        row.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task RequireDurableTenantReceiptAsync(
        TenantIdentityAuthorityPartition partition,
        InstallationIdentityCoordinatorRecord home,
        TenantSessionRevocationReceipt expected,
        CancellationToken cancellationToken)
    {
        var durable = await partition.Memberships.FinalizeSessionRevocationAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (durable != expected)
        {
            throw new InvalidOperationException(
                "identity.session_logout_receipt_mismatch: tenant receipt evidence changed.");
        }
        ValidateTenantReceipt(home, durable);
    }

    private static void ValidateTenantReceipt(
        InstallationIdentityCoordinatorRecord home,
        TenantSessionRevocationReceipt receipt)
    {
        var payload = JsonSerializer.Deserialize<LogoutPayload>(home.IntentPayloadJson, Json)
            ?? throw new InvalidOperationException(
                "identity.session_logout_receipt_invalid: logout payload is missing.");
        var expectedIntentDigest = InstallationAuditIntegrity.Hash(
            home.CorrelationId,
            home.CommandFingerprint,
            home.AccountId,
            payload.MembershipId,
            payload.SessionCorrelationId,
            home.AuthorityEvidenceDigest);
        if (receipt.TenantId != payload.TenantId ||
            receipt.MembershipId != payload.MembershipId ||
            receipt.SessionCorrelationId != payload.SessionCorrelationId ||
            receipt.DocumentOwnerVersion <= 0 ||
            receipt.AuditSequence <= 0 ||
            receipt.AuditHeadHash is not { Length: 64 } ||
            receipt.IntentDigest != expectedIntentDigest ||
            receipt.HomeDecisionDigest is not { Length: 64 })
        {
            throw new InvalidOperationException(
                "identity.session_logout_receipt_invalid: tenant evidence is not authoritative.");
        }
    }

    private async Task RequireMatchingRevocationAsync(
        WebUserSessionRecord session,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var revocation = await _sessions.FindRevocationAsync(
                session.SessionCorrelationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (revocation is null ||
            revocation.AccountId != session.AccountId ||
            revocation.CoordinationCorrelationId != correlationId ||
            revocation.ReasonCode != ReasonCode)
        {
            throw new InvalidOperationException(
                "identity.session_logout_revocation_missing: committed logout lacks exact revocation evidence.");
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
                "identity.session_logout_state_invalid: expected Finalizing.");
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
                "identity.session_logout_audit_invalid: installation audit is not authoritative.");
        }

        var now = _timeProvider.GetUtcNow();
        var envelope = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = installation.InstallationIdentityId,
            Sequence = head.Sequence + 1,
            CorrelationId = row.CorrelationId,
            CommandFingerprint = row.CommandFingerprint,
            EventType = "WebUserSessionLogoutCompleted",
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

    private static TenantSessionRevocationReceipt DeserializeReceipt(string json)
    {
        var receipts = JsonSerializer.Deserialize<TenantSessionRevocationReceipt[]>(json, Json) ?? [];
        return receipts.Length == 1
            ? receipts[0]
            : throw new InvalidOperationException(
                "identity.session_logout_receipt_invalid: one tenant receipt is required.");
    }

    private static string ComputePayloadDigest(WebUserSessionRecord session) =>
        ComputePayloadDigest(
            session.AccountId,
            new LogoutPayload(
                session.TenantId,
                session.MembershipId,
                session.SessionCorrelationId,
                session.AccountSecurityVersion,
                ReasonCode,
                session.HandleDigest));

    private static string ComputePayloadDigest(string accountId, LogoutPayload payload) =>
        InstallationAuditIntegrity.Hash(
            accountId,
            payload.TenantId,
            payload.MembershipId,
            payload.SessionCorrelationId,
            payload.AccountSecurityVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            payload.ReasonCode,
            payload.HandleDigest);

    private static string CorrelationIdFor(string sessionCorrelationId) =>
        InstallationAuditIntegrity.Hash(CommandType, sessionCorrelationId);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record LogoutPayload(
        string TenantId,
        string MembershipId,
        string SessionCorrelationId,
        long AccountSecurityVersion,
        string ReasonCode,
        string HandleDigest);
}
