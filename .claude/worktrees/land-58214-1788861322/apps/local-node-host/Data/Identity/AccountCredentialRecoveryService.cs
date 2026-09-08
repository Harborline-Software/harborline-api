using System.Data;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed outcome of one account-recovery redemption.</summary>
public enum AccountRecoveryStatus
{
    /// <summary>Credential rotated and every account session across all tenants revoked.</summary>
    Recovered,

    /// <summary>Unknown / expired / not-yet-valid / already-completed / revoked code. Non-enumerating.</summary>
    InvitationRefused,

    /// <summary>The code was resumed under a DIFFERENT new credential than first consumed. Fail-closed.</summary>
    ChangedReplay,

    /// <summary>The targeted account is missing or not active. Fail-closed.</summary>
    AccountUnavailable,
}

/// <summary>Non-secret result of one recovery.</summary>
public sealed record AccountRecoveryResult(
    AccountRecoveryStatus Status,
    string? AccountId,
    int RevokedSessionCount);

/// <summary>
/// Browser-supplied recovery command. Only the raw code proves authority; the new credential is
/// joiner input. Recovery pins the TARGET account from the signed invitation record, never from here.
/// </summary>
public sealed record AccountRecoveryCommand(
    string RawCode,
    string NewCredentialHash,
    string NewCredentialCeremonyId);

/// <summary>The public recovery-authority boundary (mirrors <c>IAccountSetupAcceptanceAuthority</c>).</summary>
public interface IAccountRecoveryAuthority
{
    /// <summary>
    /// Redeems a recovery invitation for an EXISTING account: rotates the credential, advances the
    /// account security version, and revokes every account session across all tenants and audiences.
    /// Does NOT sign the user in and creates no account, principal, Party, roster, membership, or grant.
    /// </summary>
    Task<AccountRecoveryResult> RecoverAsync(
        AccountRecoveryCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The web-plane account-recovery saga (MTW-2 #3013, ADR 0160 R3-E purpose 3 / R3-F / R3-C / D3). It
/// turns a valid single-use recovery code into a rotated credential + a fully-revoked session set:
/// <list type="number">
///   <item>Consume-or-resume the recovery code (<see cref="RecoveryInvitationStore.BeginOrResumeAsync"/>);
///     a consumed-but-incomplete recovery resumes on the same credential commitment WITHOUT
///     re-consuming (F2). The signed record pins the TARGET account.</item>
///   <item>Rotate the target account credential and advance its security version — the single lever
///     that invalidates EVERY audience of that account (each pins <c>AccountSecurityVersion</c>).
///     Idempotent on the invitation identity via its deterministic audit correlation.</item>
///   <item>Stage durable per-session revocation evidence across all tenants + all three audiences
///     (<see cref="RecoverySessionRevoker"/>) — the "assert against the session store" record.</item>
///   <item>Mark the recovery complete, closing the code to any replay.</item>
/// </list>
/// Both the version bump and the revocation staging complete BEFORE the recovery is marked successful,
/// so a redeemed code always leaves every session revoked (the credential/security-version bump makes
/// each fail live revalidation on its next request). No tenant-bound or installation session is
/// minted here; the recovered human re-authenticates through the standard login flow.
/// </summary>
internal sealed class AccountCredentialRecoveryService : IAccountRecoveryAuthority
{
    private const string RevocationReasonCode = "account-credential-recovery";

    private readonly RecoveryInvitationStore _recoveryStore;
    private readonly RecoverySessionRevoker _sessionRevoker;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityContextFactory;
    private readonly TimeProvider _timeProvider;

    public AccountCredentialRecoveryService(
        RecoveryInvitationStore recoveryStore,
        RecoverySessionRevoker sessionRevoker,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityContextFactory,
        TimeProvider timeProvider)
    {
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
        _sessionRevoker = sessionRevoker ?? throw new ArgumentNullException(nameof(sessionRevoker));
        _identityContextFactory = identityContextFactory
            ?? throw new ArgumentNullException(nameof(identityContextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AccountRecoveryResult> RecoverAsync(
        AccountRecoveryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.RawCode) ||
            string.IsNullOrWhiteSpace(command.NewCredentialHash) ||
            string.IsNullOrWhiteSpace(command.NewCredentialCeremonyId) ||
            !Argon2idCredentialArtifact.IsCanonicalAndWithinVerificationPolicy(command.NewCredentialHash))
        {
            // A malformed/weak new credential is folded into the same non-enumerating refusal as a bad
            // code: the recovery surface never stores an unvalidated credential (parity with the minter).
            return Refused();
        }

        var now = _timeProvider.GetUtcNow();
        var credentialCommitmentDigest = RecoveryInvitationStore.Digest(command.NewCredentialHash);

        // ── STEP 1: single-use consume, or resume a consumed-but-incomplete recovery (F2). ──
        var consume = await _recoveryStore
            .BeginOrResumeAsync(command.RawCode, credentialCommitmentDigest, now, cancellationToken)
            .ConfigureAwait(false);
        switch (consume.Status)
        {
            case RecoveryConsumeStatus.Refused:
                return Refused();
            case RecoveryConsumeStatus.ChangedReplay:
                return new AccountRecoveryResult(AccountRecoveryStatus.ChangedReplay, null, 0);
            case RecoveryConsumeStatus.Consumed:
            case RecoveryConsumeStatus.Resumed:
            default:
                break;
        }

        var recoveryInvitationId = consume.RecoveryInvitationId!;
        var targetAccountId = consume.TargetAccountId!;

        // ── STEP 2: rotate the credential + advance the security version (idempotent on the code). ──
        var rotated = await RotateCredentialAsync(
                recoveryInvitationId, targetAccountId, command, now, cancellationToken)
            .ConfigureAwait(false);
        if (!rotated)
        {
            return new AccountRecoveryResult(AccountRecoveryStatus.AccountUnavailable, null, 0);
        }

        // ── STEP 3: stage durable revocation evidence for every audience across all tenants. ──
        var coordinationCorrelationId = InstallationAuditIntegrity.Hash(
            "recovery-session-revocation/v1", recoveryInvitationId);
        var counts = await _sessionRevoker
            .RevokeAllForAccountAsync(
                targetAccountId, coordinationCorrelationId, RevocationReasonCode, now, cancellationToken)
            .ConfigureAwait(false);

        // ── STEP 4: mark the recovery complete, closing the code to replay. ──
        await _recoveryStore.MarkCompletedAsync(recoveryInvitationId, now, cancellationToken)
            .ConfigureAwait(false);

        return new AccountRecoveryResult(AccountRecoveryStatus.Recovered, targetAccountId, counts.Total);
    }

    /// <summary>
    /// Rotates the target account credential and advances credential + security versions in one
    /// serializable transaction, appending the tamper-evident <c>CredentialRecovered</c> audit event.
    /// Idempotent on the recovery invitation identity: if the deterministic audit envelope already
    /// exists (a prior attempt rotated), the rotation is skipped and reported as done, so a resumed
    /// recovery never double-rotates or violates the audit chain's unique correlation.
    /// </summary>
    private async Task<bool> RotateCredentialAsync(
        string recoveryInvitationId,
        string targetAccountId,
        AccountRecoveryCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var auditCorrelationId = InstallationAuditIntegrity.Hash(
            "installation-credential-recovered-audit/v1", recoveryInvitationId);
        var commandFingerprint = InstallationAuditIntegrity.Hash(
            "recovery-credential-command/v1", recoveryInvitationId, targetAccountId);

        await using var context = await _identityContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

        var account = await context.Accounts.SingleOrDefaultAsync(
            row => row.AccountId == targetAccountId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.Status != InstallationAccountStatus.Active)
        {
            return false;
        }

        var alreadyRotated = await context.AuditEnvelopes.AsNoTracking().AnyAsync(
            row => row.CorrelationId == auditCorrelationId, cancellationToken).ConfigureAwait(false);
        if (alreadyRotated)
        {
            // A prior attempt already rotated this account for this recovery; nothing more to write.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        account.CredentialHash = command.NewCredentialHash;
        account.CredentialCeremonyId = command.NewCredentialCeremonyId;
        account.CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId;
        account.CredentialVersion = checked(account.CredentialVersion + 1);
        account.SecurityVersion = checked(account.SecurityVersion + 1);
        account.OwnerVersion = checked(account.OwnerVersion + 1);
        account.UpdatedAtUtc = now;

        await InstallationIdentityAuditChain.AppendEventAsync(
            context,
            InstallationIdentityAuditEventTypes.CredentialRecovered,
            actorKind: "installation-recovery-invitation",
            actorId: recoveryInvitationId,
            correlationId: auditCorrelationId,
            commandFingerprint: commandFingerprint,
            payloadDigest: InstallationAuditIntegrity.Hash(
                targetAccountId,
                recoveryInvitationId,
                account.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            occurredAtUtc: now,
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static AccountRecoveryResult Refused() =>
        new(AccountRecoveryStatus.InvitationRefused, null, 0);
}
