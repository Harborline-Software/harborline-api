using System.Data;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed outcome of one web-plane joiner account mint.</summary>
internal enum WebJoinerAccountMintStatus
{
    /// <summary>A new installation account was created for the joiner.</summary>
    Created,

    /// <summary>The same invitation already minted this exact account; the prior row is returned.</summary>
    IdempotentReplay,

    /// <summary>The requested username is already owned by a different account. Fail-closed.</summary>
    UsernameConflict,

    /// <summary>The invitation already minted an account under different credential evidence. Fail-closed.</summary>
    ChangedReplay,
}

/// <summary>Secret-bearing joiner account command; accepted only by the acceptance authority.</summary>
internal sealed record WebJoinerAccountMintCommand(
    string InvitationId,
    string TenantId,
    string Username,
    string CredentialHash,
    string CredentialCeremonyId);

/// <summary>Non-secret result from one joiner account mint.</summary>
internal sealed record WebJoinerAccountMintResult(
    WebJoinerAccountMintStatus Status,
    string? AccountId);

/// <summary>
/// Mints the web-plane joiner's installation account from an accepted account-setup invitation
/// (MTW-2 #2614, Reading B element (1)). It creates ONLY an <see cref="InstallationAccountRecord"/>
/// (username + canonical Argon2id credential, per ADR 0097) plus a tamper-evident
/// <see cref="InstallationIdentityAuditEventTypes.AccountAdmitted"/> envelope on the installation
/// chain, both in one serializable transaction. It holds no tenant, Party, principal, grant, or
/// role authority — the acceptance saga mints those in later idempotent steps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent on the invitation identity (D2 discipline).</b> The account id is a deterministic
/// digest of the invitation id, so a replayed acceptance resolves the SAME account. A retry with the
/// same credential evidence returns <see cref="WebJoinerAccountMintStatus.IdempotentReplay"/>; a
/// retry that changed the username/credential returns <see cref="WebJoinerAccountMintStatus.ChangedReplay"/>
/// and mints nothing. A username already owned by a DIFFERENT invitation's account returns
/// <see cref="WebJoinerAccountMintStatus.UsernameConflict"/> (the unique username index is the
/// authority — this minter never renames or merges).
/// </para>
/// <para>
/// <b>Registered non-singleton (admiral-ruling-2026-07-22T2310Z).</b> A fresh instance is resolved
/// per acceptance so no mint state is shared across requests.
/// </para>
/// </remarks>
internal sealed class WebJoinerAccountMinter
{
    private const int BusyRetryCount = 8;

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public WebJoinerAccountMinter(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// The deterministic joiner account id derived from the invitation identity. Exposed so the
    /// acceptance saga can key its later idempotent steps (Party binding, grant, membership) to the
    /// same account without re-reading it.
    /// </summary>
    internal static string AccountIdFor(string invitationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationId);
        return InstallationAuditIntegrity.Hash("web-joiner-account/v1", invitationId);
    }

    public async Task<WebJoinerAccountMintResult> MintAsync(
        WebJoinerAccountMintCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.InvitationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);

        var normalizedUsername = NormalizeUsername(command.Username);
        ValidateCredential(command);
        var credentialCeremonyId = command.CredentialCeremonyId.ToLowerInvariant();
        var accountId = AccountIdFor(command.InvitationId);
        var commandFingerprint = InstallationAuditIntegrity.Hash(
            "web-joiner-account-command/v1",
            command.InvitationId,
            normalizedUsername,
            credentialCeremonyId,
            command.CredentialHash);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryMintAsync(
                        command,
                        accountId,
                        normalizedUsername,
                        credentialCeremonyId,
                        commandFingerprint,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<WebJoinerAccountMintResult> TryMintAsync(
        WebJoinerAccountMintCommand command,
        string accountId,
        string normalizedUsername,
        string credentialCeremonyId,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var existing = await context.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return ResolveExisting(existing, normalizedUsername, credentialCeremonyId, command.CredentialHash);
        }

        var usernameOwner = await context.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(row => row.NormalizedUsername == normalizedUsername, cancellationToken)
            .ConfigureAwait(false);
        if (usernameOwner is not null)
        {
            return new WebJoinerAccountMintResult(WebJoinerAccountMintStatus.UsernameConflict, null);
        }

        try
        {
            context.Accounts.Add(new InstallationAccountRecord
            {
                AccountId = accountId,
                NormalizedUsername = normalizedUsername,
                CredentialHash = command.CredentialHash,
                CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
                CredentialCeremonyId = credentialCeremonyId,
                CredentialVersion = 1,
                Status = InstallationAccountStatus.Active,
                SecurityVersion = 1,
                OwnerVersion = 1,
                CreatedAtUtc = occurredAtUtc,
                UpdatedAtUtc = occurredAtUtc,
            });

            // Durable, tamper-evident audit appended to the installation chain in the SAME
            // transaction as the account row, so the joiner-account seam always leaves a record.
            await InstallationIdentityAuditChain.AppendEventAsync(
                    context,
                    InstallationIdentityAuditEventTypes.AccountAdmitted,
                    actorKind: "installation-invitation",
                    actorId: command.InvitationId,
                    correlationId: InstallationAuditIntegrity.Hash(
                        "installation-account-admitted-audit/v1", accountId),
                    commandFingerprint: commandFingerprint,
                    payloadDigest: InstallationAuditIntegrity.Hash(
                        accountId,
                        command.TenantId,
                        command.InvitationId,
                        normalizedUsername),
                    occurredAtUtc: occurredAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WebJoinerAccountMintResult(WebJoinerAccountMintStatus.Created, accountId);
        }
        catch (DbUpdateException exception) when (IsUniquenessConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var winner = await context.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(row => row.AccountId == accountId, cancellationToken)
                .ConfigureAwait(false);
            return winner is not null
                ? ResolveExisting(winner, normalizedUsername, credentialCeremonyId, command.CredentialHash)
                : new WebJoinerAccountMintResult(WebJoinerAccountMintStatus.UsernameConflict, null);
        }
    }

    private static WebJoinerAccountMintResult ResolveExisting(
        InstallationAccountRecord existing,
        string normalizedUsername,
        string credentialCeremonyId,
        string credentialHash)
    {
        var sameEvidence =
            string.Equals(existing.NormalizedUsername, normalizedUsername, StringComparison.Ordinal) &&
            string.Equals(existing.CredentialCeremonyId, credentialCeremonyId, StringComparison.Ordinal) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(existing.CredentialHash),
                Encoding.UTF8.GetBytes(credentialHash));
        return sameEvidence
            ? new WebJoinerAccountMintResult(WebJoinerAccountMintStatus.IdempotentReplay, existing.AccountId)
            : new WebJoinerAccountMintResult(WebJoinerAccountMintStatus.ChangedReplay, null);
    }

    private static string NormalizeUsername(string username)
    {
        return WebUsernameNormalizer.NormalizeRequired(username);
    }

    private static void ValidateCredential(WebJoinerAccountMintCommand command)
    {
        if (!Argon2idCredentialArtifact.IsCanonicalAndWithinVerificationPolicy(command.CredentialHash))
        {
            throw new ArgumentException(
                "Credential artifact must be canonical Argon2id v1.3 at or above the policy floor.",
                nameof(command));
        }

        if (!Guid.TryParseExact(command.CredentialCeremonyId, "N", out _))
        {
            throw new ArgumentException(
                "Credential ceremony id must be a 32-character UUID without separators.",
                nameof(command));
        }
    }

    private static bool IsRetryableContention(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 } ||
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };

    private static bool IsUniquenessConflict(Exception exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };
}
