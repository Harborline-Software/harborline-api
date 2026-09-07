using System.Data;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Issuance seed for a recovery invitation targeting an EXISTING account.</summary>
internal sealed record RecoveryInvitationSeed(
    string TenantId,
    string IssuerAccountId,
    string IssuerPrincipalId,
    string TargetAccountId,
    string TargetNormalizedUsername,
    string CommandFingerprint,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>Non-secret result of issuing a recovery invitation; the raw code is returned exactly once.</summary>
internal sealed record RecoveryInvitationIssueResult(
    string RecoveryInvitationId,
    string RawCode,
    string TenantId,
    string TargetAccountId,
    DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>The typed outcome of a begin-or-resume of a recovery redemption.</summary>
internal enum RecoveryConsumeStatus
{
    /// <summary>Unknown / expired / not-yet-valid / already-consumed-and-completed / revoked. Non-enumerating.</summary>
    Refused,

    /// <summary>Fresh single-use consume: the code was just now marked consumed.</summary>
    Consumed,

    /// <summary>
    /// The code was already consumed but the recovery never completed (F2). The SAME credential
    /// commitment was re-presented, so the caller may re-drive the remaining recovery steps WITHOUT
    /// re-consuming.
    /// </summary>
    Resumed,

    /// <summary>The code was consumed under a DIFFERENT credential commitment. Fail-closed.</summary>
    ChangedReplay,
}

/// <summary>The server-resolved pins recovered when a recovery invitation is consumed.</summary>
internal sealed record RecoveryInvitationConsumeResult(
    RecoveryConsumeStatus Status,
    string? RecoveryInvitationId,
    string? TenantId,
    string? IssuerAccountId,
    string? TargetAccountId,
    string? TargetNormalizedUsername,
    string? CommandFingerprint);

/// <summary>
/// Persists only a digest of each 256-bit recovery code (MTW-2 #3013). Deliberately a separate table
/// from <see cref="AccountSetupInvitationStore"/>: the recovery code lives only here, so it is
/// structurally purpose-bound (a recovery digest can never be consumed on the account-setup path or
/// vice-versa). Redemption is single-use across restart; a consumed-but-incomplete recovery is
/// resumable on the invitation identity (F2) without re-consuming.
/// </summary>
internal sealed class RecoveryInvitationStore(
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory)
{
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<RecoveryInvitationIssueResult?> IssueAsync(
        RecoveryInvitationSeed seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        Validate(seed);

        var rawCode = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var row = new RecoveryInvitationRecord
        {
            RecoveryInvitationId = RandomHex(32),
            TenantId = seed.TenantId,
            IssuerAccountId = seed.IssuerAccountId,
            IssuerPrincipalId = seed.IssuerPrincipalId,
            TargetAccountId = seed.TargetAccountId,
            TargetNormalizedUsername = seed.TargetNormalizedUsername,
            TokenDigest = Digest(rawCode),
            CommandFingerprint = seed.CommandFingerprint,
            IssuedAtUtc = seed.IssuedAtUtc,
            AbsoluteExpiresAtUtc = seed.AbsoluteExpiresAtUtc,
            ConsumedAtUtc = null,
            CredentialCommitmentDigest = null,
            CompletedAtUtc = null,
            RevokedAtUtc = null,
            OwnerVersion = 1,
        };

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        context.RecoveryInvitations.Add(row);

        await InstallationIdentityAuditChain.AppendEventAsync(
            context,
            InstallationIdentityAuditEventTypes.RecoveryInvitationIssued,
            actorKind: "installation-account",
            actorId: seed.IssuerAccountId,
            correlationId: InstallationAuditIntegrity.Hash(
                "installation-recovery-invitation-issued-audit/v1", row.RecoveryInvitationId),
            commandFingerprint: seed.CommandFingerprint,
            payloadDigest: InstallationAuditIntegrity.Hash(
                row.RecoveryInvitationId,
                seed.TenantId,
                seed.IssuerAccountId,
                seed.TargetAccountId),
            occurredAtUtc: seed.IssuedAtUtc,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryInvitationIssueResult(
                row.RecoveryInvitationId,
                rawCode,
                row.TenantId,
                row.TargetAccountId,
                row.AbsoluteExpiresAtUtc);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Atomically begins (or resumes) a single-use recovery redemption. On a fresh code it marks the
    /// row consumed and pins the credential commitment; on a consumed-but-incomplete row it returns
    /// <see cref="RecoveryConsumeStatus.Resumed"/> if the SAME commitment is re-presented (F2) or
    /// <see cref="RecoveryConsumeStatus.ChangedReplay"/> otherwise. Unknown / expired / revoked /
    /// already-completed all return the single non-enumerating <see cref="RecoveryConsumeStatus.Refused"/>.
    /// </summary>
    public async Task<RecoveryInvitationConsumeResult> BeginOrResumeAsync(
        string rawCode,
        string credentialCommitmentDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialCommitmentDigest);

        var digest = Digest(rawCode);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await context.RecoveryInvitations.SingleOrDefaultAsync(
            item => item.TokenDigest == digest,
            cancellationToken).ConfigureAwait(false);
        if (row is null || row.RevokedAtUtc is not null ||
            now < row.IssuedAtUtc || now >= row.AbsoluteExpiresAtUtc)
        {
            return Refused();
        }

        // Already completed → replay refused.
        if (row.CompletedAtUtc is not null)
        {
            return Refused();
        }

        RecoveryConsumeStatus status;
        if (row.ConsumedAtUtc is null)
        {
            // Fresh single-use consume: pin the credential commitment.
            row.ConsumedAtUtc = now;
            row.CredentialCommitmentDigest = credentialCommitmentDigest;
            row.OwnerVersion = checked(row.OwnerVersion + 1);
            status = RecoveryConsumeStatus.Consumed;
        }
        else if (CryptographicOperations.FixedTimeEquals(
                     Encoding.ASCII.GetBytes(row.CredentialCommitmentDigest ?? string.Empty),
                     Encoding.ASCII.GetBytes(credentialCommitmentDigest)))
        {
            // Consumed-but-incomplete + same commitment → resume without re-consuming (F2).
            status = RecoveryConsumeStatus.Resumed;
        }
        else
        {
            return new RecoveryInvitationConsumeResult(
                RecoveryConsumeStatus.ChangedReplay, null, null, null, null, null, null);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryInvitationConsumeResult(
                status,
                row.RecoveryInvitationId,
                row.TenantId,
                row.IssuerAccountId,
                row.TargetAccountId,
                row.TargetNormalizedUsername,
                row.CommandFingerprint);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Refused();
        }
    }

    /// <summary>Marks a consumed recovery invitation fully completed, closing it to any replay.</summary>
    public async Task MarkCompletedAsync(
        string recoveryInvitationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryInvitationId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await context.RecoveryInvitations.SingleOrDefaultAsync(
            item => item.RecoveryInvitationId == recoveryInvitationId,
            cancellationToken).ConfigureAwait(false);
        if (row is null || row.ConsumedAtUtc is null || row.CompletedAtUtc is not null)
        {
            return;
        }

        row.CompletedAtUtc = now;
        row.OwnerVersion = checked(row.OwnerVersion + 1);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> RevokeAsync(
        string recoveryInvitationId,
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryInvitationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await context.RecoveryInvitations.SingleOrDefaultAsync(
            item => item.RecoveryInvitationId == recoveryInvitationId && item.TenantId == tenantId,
            cancellationToken).ConfigureAwait(false);
        if (row is null || row.ConsumedAtUtc is not null || row.RevokedAtUtc is not null ||
            now < row.IssuedAtUtc || now >= row.AbsoluteExpiresAtUtc)
        {
            return false;
        }

        row.RevokedAtUtc = now;
        row.OwnerVersion = checked(row.OwnerVersion + 1);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private static RecoveryInvitationConsumeResult Refused() =>
        new(RecoveryConsumeStatus.Refused, null, null, null, null, null, null);

    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RandomHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private static void Validate(RecoveryInvitationSeed seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.IssuerAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.IssuerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.TargetAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.TargetNormalizedUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.CommandFingerprint);
        if (seed.AbsoluteExpiresAtUtc <= seed.IssuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seed),
                "Recovery invitation expiry must be after issuance.");
        }
    }
}
