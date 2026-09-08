using System.Data;
using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The outcome of one initial installation-root binding attempt.</summary>
public enum InstallationFounderBindingStatus
{
    Created,
    IdempotentReplay,
    AlreadyDesignated,
    ChangedReplay,
}

/// <summary>
/// Binds an existing installation account to verified canonical founder evidence. Party and
/// principal coordinates remain owned by their canonical stores and are persisted here only as a
/// digest.
/// </summary>
public sealed record InstallationFounderBindingCommand(
    string AccountId,
    CanonicalPartyBinding FounderBinding,
    long ExpectedSourceVersion,
    string IdempotencyKey,
    string AuditCorrelationId);

/// <summary>Non-secret durable receipt for a completed founder binding.</summary>
public sealed record InstallationFounderBindingReceipt(
    string DesignationId,
    string AccountId,
    string SourceCompositeKeyDigest,
    long ExpectedSourceVersion,
    string IdempotencyKeyDigest,
    string AuditCorrelationId,
    long OwnerVersion,
    DateTimeOffset DesignatedAtUtc,
    DateTimeOffset? VerifiedAtUtc);

/// <summary>Result of a founder binding attempt.</summary>
public sealed record InstallationFounderBindingResult(
    InstallationFounderBindingStatus Status,
    InstallationFounderBindingReceipt? Receipt);

/// <summary>
/// Locates completed founder-binding receipts by a caller's idempotency key. The raw key is never
/// persisted, and locating a receipt does not make copied Party or principal data authoritative.
/// </summary>
public sealed class InstallationFounderCompletedReceiptLocator
{
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;

    public InstallationFounderCompletedReceiptLocator(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<InstallationFounderBindingReceipt?> FindAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var digest = InstallationFounderBindingService.DigestIdempotencyKey(idempotencyKey);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FindByDigestAsync(context, digest, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<InstallationFounderBindingReceipt?> FindByDigestAsync(
        NodeLocalInstallationIdentityDbContext context,
        string idempotencyKeyDigest,
        CancellationToken cancellationToken)
    {
        var record = await context.RootDesignations.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.IdempotencyKeyDigest == idempotencyKeyDigest,
                cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToReceipt(record);
    }

    internal static InstallationFounderBindingReceipt ToReceipt(
        InstallationIdentityRootDesignationRecord record) =>
        new(
            record.DesignationId,
            record.AccountId ?? throw new InvalidOperationException(
                "installation-identity.root_designation_account_missing"),
            record.SourceCompositeKeyDigest,
            record.ExpectedSourceVersion,
            record.IdempotencyKeyDigest,
            record.AuditCorrelationId,
            record.OwnerVersion,
            record.DesignatedAtUtc,
            record.VerifiedAtUtc);
}

/// <summary>
/// Writes the one-time initial installation-root designation after first checking for a completed
/// idempotent receipt. Exposed to the web surface by exactly ONE consumer —
/// <see cref="WebFounderBindAuthority"/>, reached only from the selected-session-gated
/// <c>POST /api/session/founder-bind</c> route. No other route, service, or DI registration may
/// reach it.
/// </summary>
public sealed class InstallationFounderBindingService
{
    private const int BusyRetryCount = 8;
    private const int MaximumIdempotencyKeyLength = 512;

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public InstallationFounderBindingService(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<InstallationFounderBindingResult> BindAsync(
        InstallationFounderBindingCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command);
        var idempotencyKeyDigest = DigestIdempotencyKey(command.IdempotencyKey);
        var sourceCompositeKeyDigest = InstallationAuditIntegrity.Hash(
            "installation-founder-canonical-binding/v1",
            command.FounderBinding.VerifiedTenant.Value,
            command.FounderBinding.PrincipalUserId.Value,
            command.FounderBinding.PartyId.Value);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryBindAsync(
                    command,
                    idempotencyKeyDigest,
                    sourceCompositeKeyDigest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static string DigestIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > MaximumIdempotencyKeyLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idempotencyKey),
                $"Idempotency key exceeds {MaximumIdempotencyKeyLength} characters.");
        }

        return InstallationAuditIntegrity.Hash(
            "installation-founder-binding-idempotency/v1",
            idempotencyKey);
    }

    private async Task<InstallationFounderBindingResult> TryBindAsync(
        InstallationFounderBindingCommand command,
        string idempotencyKeyDigest,
        string sourceCompositeKeyDigest,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        // This completed-receipt lookup is intentionally the first authority-store read. Removing
        // it changes a retry into AlreadyDesignated and is caught by the mutation-proof regression.
        var completedReceipt = await InstallationFounderCompletedReceiptLocator.FindByDigestAsync(
            context,
            idempotencyKeyDigest,
            cancellationToken).ConfigureAwait(false);
        if (completedReceipt is not null)
        {
            return ResolveReplay(command, sourceCompositeKeyDigest, completedReceipt);
        }

        var existingDesignation = await context.RootDesignations.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.SingletonKey ==
                    InstallationIdentityRootDesignationRecord.SingletonKeyValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingDesignation is not null)
        {
            return new InstallationFounderBindingResult(
                InstallationFounderBindingStatus.AlreadyDesignated,
                null);
        }

        var account = await context.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AccountId == command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null || account.Status != InstallationAccountStatus.Active)
        {
            throw new InvalidOperationException(
                "installation-identity.founder_account_not_active: the designated account must exist and be active.");
        }

        var now = _timeProvider.GetUtcNow();
        var record = new InstallationIdentityRootDesignationRecord
        {
            SingletonKey = InstallationIdentityRootDesignationRecord.SingletonKeyValue,
            DesignationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            SourceCompositeKeyDigest = sourceCompositeKeyDigest,
            AccountId = command.AccountId,
            ExpectedSourceVersion = command.ExpectedSourceVersion,
            IdempotencyKeyDigest = idempotencyKeyDigest,
            AuditCorrelationId = command.AuditCorrelationId,
            OwnerVersion = 1,
            DesignatedAtUtc = now,
            VerifiedAtUtc = now,
        };
        context.RootDesignations.Add(record);

        // Durable, tamper-evident audit of the now-live founder-bind route (PR #3008), appended to
        // the existing installation chain in the SAME transaction as the RootDesignation mutation.
        await InstallationIdentityAuditChain.AppendEventAsync(
            context,
            InstallationIdentityAuditEventTypes.FounderBindingDesignated,
            actorKind: "installation-account",
            actorId: command.AccountId,
            correlationId: InstallationAuditIntegrity.Hash(
                "installation-founder-binding-audit/v1", record.DesignationId),
            commandFingerprint: sourceCompositeKeyDigest,
            payloadDigest: InstallationAuditIntegrity.Hash(
                record.DesignationId,
                command.AccountId,
                record.SourceCompositeKeyDigest,
                record.ExpectedSourceVersion.ToString(CultureInfo.InvariantCulture),
                record.IdempotencyKeyDigest),
            occurredAtUtc: now,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new InstallationFounderBindingResult(
                InstallationFounderBindingStatus.Created,
                InstallationFounderCompletedReceiptLocator.ToReceipt(record));
        }
        catch (DbUpdateException exception) when (IsUniquenessConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            var winner = await InstallationFounderCompletedReceiptLocator.FindByDigestAsync(
                context,
                idempotencyKeyDigest,
                cancellationToken).ConfigureAwait(false);
            if (winner is not null)
            {
                return ResolveReplay(command, sourceCompositeKeyDigest, winner);
            }

            return new InstallationFounderBindingResult(
                InstallationFounderBindingStatus.AlreadyDesignated,
                null);
        }
    }

    private static InstallationFounderBindingResult ResolveReplay(
        InstallationFounderBindingCommand command,
        string sourceCompositeKeyDigest,
        InstallationFounderBindingReceipt receipt)
    {
        var sameCommand = string.Equals(receipt.AccountId, command.AccountId, StringComparison.Ordinal) &&
            string.Equals(
                receipt.SourceCompositeKeyDigest,
                sourceCompositeKeyDigest,
                StringComparison.Ordinal) &&
            receipt.ExpectedSourceVersion == command.ExpectedSourceVersion;
        return sameCommand
            ? new InstallationFounderBindingResult(
                InstallationFounderBindingStatus.IdempotentReplay,
                receipt)
            : new InstallationFounderBindingResult(
                InstallationFounderBindingStatus.ChangedReplay,
                null);
    }

    private static void ValidateCommand(InstallationFounderBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.FounderBinding);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AuditCorrelationId);
        _ = DigestIdempotencyKey(command.IdempotencyKey);

        if (command.AccountId.Length > 64 || command.AuditCorrelationId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "A founder binding authority field exceeds its durable-store limit.");
        }

        if (command.ExpectedSourceVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Expected source version must be positive.");
        }
    }

    private static bool IsRetryableContention(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 } ||
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };

    private static bool IsUniquenessConflict(Exception exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };
}
