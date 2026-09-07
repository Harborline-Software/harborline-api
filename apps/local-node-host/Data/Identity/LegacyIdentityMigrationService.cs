using System.Data;
using System.Globalization;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The frozen, source-qualified key consumed by the legacy rename ceremony.</summary>
internal sealed record LegacyWebAccountCompositeKey(
    string SourceKind,
    string SourcePartition,
    string LegacyAccountId)
{
    internal string CanonicalValue => LegacyIdentityCanonicalFraming.Frame(
        SourceKind,
        SourcePartition,
        LegacyAccountId);
}

internal static class LegacyIdentityCanonicalFraming
{
    internal static string Frame(params string[] values) =>
        string.Join("|", values.Select(Encode));

    private static string Encode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return $"{Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)}:{value}";
    }
}

/// <summary>One DB-backed legacy row returned by the migration-only read contract.</summary>
internal sealed record DbBackedLegacyWebIdentityRow(
    string LegacyAccountId,
    string Username,
    long OwnerVersion,
    bool HasCredential,
    bool IsUncredentialedInvite,
    string AuditCorrelationId);

/// <summary>A repeatable DB partition snapshot. Callers never infer a watermark from row order.</summary>
internal sealed record DbBackedLegacyWebIdentitySnapshot(
    string SourcePartition,
    long SourceVersion,
    string HighWatermark,
    IReadOnlyList<DbBackedLegacyWebIdentityRow> Rows);

internal enum LegacyWebIdentitySourceRenameStatus
{
    Renamed,
    IdempotentReplay,
    SourceNotFound,
    SourceVersionStale,
    TargetUsernameCollision,
    ChangedReplay,
}

internal sealed record LegacyWebIdentitySourceRenameResult(LegacyWebIdentitySourceRenameStatus Status);

/// <summary>
/// Mutation-minimal legacy DB seam. Implementations may change only the normalized username and
/// owner version for the selected composite key; credentials and tenant authority are out of scope.
/// </summary>
internal interface IDbBackedLegacyWebIdentityStore
{
    Task<IReadOnlyList<DbBackedLegacyWebIdentitySnapshot>> ReadSnapshotsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically verifies target-name availability and renames the source row. A concurrent target
    /// claim must return <see cref="LegacyWebIdentitySourceRenameStatus.TargetUsernameCollision"/>.
    /// </summary>
    Task<LegacyWebIdentitySourceRenameResult> RenameAsync(
        LegacyWebAccountCompositeKey key,
        string normalizedUsername,
        long expectedOwnerVersion,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal enum LegacyWebIdentitySourceMutability
{
    DatabaseBacked,
    ImmutableOptionsFounder,
}

/// <summary>Canonical internal row; raw usernames never appear in classified preview output.</summary>
internal sealed record LegacyWebIdentityInventoryRow(
    LegacyWebAccountCompositeKey CompositeKey,
    string DeterministicInstallationAccountId,
    string NormalizedUsername,
    long OwnerVersion,
    long SourceVersion,
    string SourceHighWatermark,
    bool HasCredential,
    bool IsUncredentialedInvite,
    string AuditCorrelationId,
    LegacyWebIdentitySourceMutability Mutability);

internal sealed record LegacyIdentitySourceWatermark(
    string SourceKind,
    string SourcePartition,
    long SourceVersion,
    string HighWatermark,
    string SnapshotDigest);

internal enum LegacyUsernameCollisionRepairability
{
    RenameEligible,
    ImmutableSourceRefusal,
}

/// <summary>Classified-safe collision evidence: digest, count, and repairability only.</summary>
internal sealed record LegacyUsernameCollisionPreview(
    string CollisionId,
    int CandidateCount,
    LegacyUsernameCollisionRepairability Repairability);

internal sealed record LegacyWebIdentityInventory(
    IReadOnlyList<LegacyWebIdentityInventoryRow> Rows,
    IReadOnlyList<LegacyIdentitySourceWatermark> Watermarks,
    IReadOnlyList<LegacyUsernameCollisionPreview> Collisions,
    string SnapshotDigest);

/// <summary>
/// MIG-01B read-only inventory over both v1 sources. It sorts before hashing and owns no DbContext,
/// so previewing cannot advance a watermark or mutate migration state.
/// </summary>
internal sealed class LegacyWebIdentityInventoryReader
{
    internal const string DatabaseSourceKind = "legacy-db-web-identity";
    internal const string OptionsFounderSourceKind = "node-web-client-options-founder";
    internal const string OptionsFounderPartition = "singleton";
    internal const string OptionsFounderAccountId = "founder";
    internal const long ImmutableOptionsOwnerVersion = 0;

    private readonly IDbBackedLegacyWebIdentityStore _databaseSource;
    private readonly NodeWebClientOptions _options;

    internal LegacyWebIdentityInventoryReader(
        IDbBackedLegacyWebIdentityStore databaseSource,
        NodeWebClientOptions options)
    {
        _databaseSource = databaseSource ?? throw new ArgumentNullException(nameof(databaseSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async Task<LegacyWebIdentityInventory> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshots = await _databaseSource.ReadSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(snapshots);
        var orderedSnapshots = snapshots
            .OrderBy(item => item.SourcePartition, StringComparer.Ordinal)
            .ToArray();
        if (orderedSnapshots
            .GroupBy(item => item.SourcePartition, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("identity.legacy_source_partition_duplicate");
        }
        var rows = new List<LegacyWebIdentityInventoryRow>();
        var watermarks = new List<LegacyIdentitySourceWatermark>();

        foreach (var snapshot in orderedSnapshots)
        {
            ValidateSnapshot(snapshot);
            var partitionRows = snapshot.Rows
                .Select(row => ToInventoryRow(snapshot, row))
                .OrderBy(row => row.CompositeKey.CanonicalValue, StringComparer.Ordinal)
                .ToArray();
            rows.AddRange(partitionRows);
            watermarks.Add(new LegacyIdentitySourceWatermark(
                DatabaseSourceKind,
                snapshot.SourcePartition,
                snapshot.SourceVersion,
                snapshot.HighWatermark,
                ComputePartitionDigest(partitionRows)));
        }

        var founderUsername = _options.FounderUsername;
        if (!string.IsNullOrWhiteSpace(founderUsername))
        {
            var founder = new LegacyWebIdentityInventoryRow(
                new LegacyWebAccountCompositeKey(
                    OptionsFounderSourceKind,
                    OptionsFounderPartition,
                    OptionsFounderAccountId),
                DeterministicAccountId(
                    new LegacyWebAccountCompositeKey(
                        OptionsFounderSourceKind,
                        OptionsFounderPartition,
                        OptionsFounderAccountId)),
                NormalizeUsername(founderUsername),
                ImmutableOptionsOwnerVersion,
                ImmutableOptionsOwnerVersion,
                "immutable:0",
                !string.IsNullOrWhiteSpace(_options.FounderPasswordHash),
                IsUncredentialedInvite: false,
                AuditCorrelationId: "options-founder-immutable-source",
                LegacyWebIdentitySourceMutability.ImmutableOptionsFounder);
            rows.Add(founder);
            watermarks.Add(new LegacyIdentitySourceWatermark(
                OptionsFounderSourceKind,
                OptionsFounderPartition,
                ImmutableOptionsOwnerVersion,
                "immutable:0",
                ComputePartitionDigest([founder])));
        }

        var canonicalRows = rows
            .OrderBy(row => row.CompositeKey.CanonicalValue, StringComparer.Ordinal)
            .ToArray();
        var canonicalWatermarks = watermarks
            .OrderBy(item => item.SourceKind, StringComparer.Ordinal)
            .ThenBy(item => item.SourcePartition, StringComparer.Ordinal)
            .ToArray();
        var collisions = canonicalRows
            .GroupBy(row => row.NormalizedUsername, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new LegacyUsernameCollisionPreview(
                CollisionId(group.Key),
                group.Count(),
                group.Any(row => row.Mutability == LegacyWebIdentitySourceMutability.ImmutableOptionsFounder)
                    ? LegacyUsernameCollisionRepairability.ImmutableSourceRefusal
                    : LegacyUsernameCollisionRepairability.RenameEligible))
            .OrderBy(collision => collision.CollisionId, StringComparer.Ordinal)
            .ToArray();
        var snapshotDigest = InstallationAuditIntegrity.Hash(
            [
                "legacy-web-inventory-v1",
                .. canonicalWatermarks.Select(CanonicalWatermark),
                .. canonicalRows.Select(CanonicalRow),
            ]);

        return new LegacyWebIdentityInventory(
            canonicalRows,
            canonicalWatermarks,
            collisions,
            snapshotDigest);
    }

    internal static string NormalizeUsername(string username)
        => WebUsernameNormalizer.NormalizeRequired(username);

    internal static string CollisionId(string normalizedUsername) =>
        InstallationAuditIntegrity.Hash("legacy-username-collision-v1", normalizedUsername);

    private static LegacyWebIdentityInventoryRow ToInventoryRow(
        DbBackedLegacyWebIdentitySnapshot snapshot,
        DbBackedLegacyWebIdentityRow row)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(row.LegacyAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.AuditCorrelationId);
        if (row.OwnerVersion <= 0)
        {
            throw new InvalidOperationException("identity.legacy_source_owner_version_invalid");
        }
        if (row.IsUncredentialedInvite && row.HasCredential)
        {
            throw new InvalidOperationException("identity.legacy_invite_credential_state_invalid");
        }

        var key = new LegacyWebAccountCompositeKey(
            DatabaseSourceKind,
            snapshot.SourcePartition,
            row.LegacyAccountId);
        return new LegacyWebIdentityInventoryRow(
            key,
            DeterministicAccountId(key),
            NormalizeUsername(row.Username),
            row.OwnerVersion,
            snapshot.SourceVersion,
            snapshot.HighWatermark,
            row.HasCredential,
            row.IsUncredentialedInvite,
            row.AuditCorrelationId,
            LegacyWebIdentitySourceMutability.DatabaseBacked);
    }

    private static void ValidateSnapshot(DbBackedLegacyWebIdentitySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SourcePartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.HighWatermark);
        ArgumentNullException.ThrowIfNull(snapshot.Rows);
        if (snapshot.SourceVersion < 0)
        {
            throw new InvalidOperationException("identity.legacy_source_version_invalid");
        }
        if (snapshot.Rows.Select(row => row.LegacyAccountId).Distinct(StringComparer.Ordinal).Count()
            != snapshot.Rows.Count)
        {
            throw new InvalidOperationException("identity.legacy_source_composite_key_duplicate");
        }
    }

    private static string DeterministicAccountId(LegacyWebAccountCompositeKey key) =>
        InstallationAuditIntegrity.Hash("legacy-installation-account-remap-v1", key.CanonicalValue);

    private static string ComputePartitionDigest(IReadOnlyList<LegacyWebIdentityInventoryRow> rows) =>
        InstallationAuditIntegrity.Hash(["legacy-source-partition-v1", .. rows.Select(CanonicalRow)]);

    private static string CanonicalRow(LegacyWebIdentityInventoryRow row) =>
        LegacyIdentityCanonicalFraming.Frame(
            row.CompositeKey.CanonicalValue,
            row.DeterministicInstallationAccountId,
            InstallationAuditIntegrity.Hash("normalized-username-v1", row.NormalizedUsername),
            row.OwnerVersion.ToString(CultureInfo.InvariantCulture),
            row.SourceVersion.ToString(CultureInfo.InvariantCulture),
            row.SourceHighWatermark,
            row.HasCredential ? "credentialed" : "uncredentialed",
            row.IsUncredentialedInvite ? "invite" : "account",
            InstallationAuditIntegrity.Hash("source-audit-correlation-v1", row.AuditCorrelationId),
            row.Mutability.ToString());

    private static string CanonicalWatermark(LegacyIdentitySourceWatermark watermark) =>
        LegacyIdentityCanonicalFraming.Frame(
            watermark.SourceKind,
            watermark.SourcePartition,
            watermark.SourceVersion.ToString(CultureInfo.InvariantCulture),
            watermark.HighWatermark,
            watermark.SnapshotDigest);
}

internal sealed record LegacyInviteTombstoneProjectionResult(int CreatedCount, int ReplayCount);

/// <summary>MIG-02A projection. It creates only tombstones and preserves source audit provenance.</summary>
internal sealed class LegacyInviteTombstoneProjector
{
    internal const string UncredentialedInviteReason = "legacy_uncredentialed_invite_omitted";

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    internal LegacyInviteTombstoneProjector(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task<LegacyInviteTombstoneProjectionResult> ProjectAsync(
        LegacyWebIdentityInventory inventory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var candidates = inventory.Rows
            .Where(row => row.Mutability == LegacyWebIdentitySourceMutability.DatabaseBacked
                && row.IsUncredentialedInvite
                && !row.HasCredential)
            .OrderBy(row => row.CompositeKey.CanonicalValue, StringComparer.Ordinal)
            .ToArray();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var created = 0;
        var replayed = 0;
        foreach (var candidate in candidates)
        {
            var sourceKeyDigest = InstallationAuditIntegrity.Hash(
                "legacy-source-composite-key-v1",
                candidate.CompositeKey.CanonicalValue);
            var tombstoneId = InstallationAuditIntegrity.Hash(
                "legacy-uncredentialed-invite-tombstone-v1",
                candidate.CompositeKey.CanonicalValue);
            var projectionDigest = InstallationAuditIntegrity.Hash(
                "legacy-uncredentialed-invite-projection-v1",
                sourceKeyDigest,
                candidate.SourceVersion.ToString(CultureInfo.InvariantCulture),
                candidate.AuditCorrelationId,
                UncredentialedInviteReason);
            var existing = await context.MigrationTombstones.SingleOrDefaultAsync(
                row => row.TombstoneId == tombstoneId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.SourceKind, candidate.CompositeKey.SourceKind, StringComparison.Ordinal)
                    || !string.Equals(existing.SourceKeyDigest, sourceKeyDigest, StringComparison.Ordinal)
                    || existing.SourceVersion != candidate.SourceVersion
                    || !string.Equals(existing.ReasonCode, UncredentialedInviteReason, StringComparison.Ordinal)
                    || !string.Equals(existing.AuditCorrelationId, candidate.AuditCorrelationId, StringComparison.Ordinal)
                    || !string.Equals(existing.ProjectionDigest, projectionDigest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("identity.legacy_tombstone_replay_changed");
                }

                replayed++;
                continue;
            }

            context.MigrationTombstones.Add(new InstallationIdentityMigrationTombstoneRecord
            {
                TombstoneId = tombstoneId,
                SourceKind = candidate.CompositeKey.SourceKind,
                SourceKeyDigest = sourceKeyDigest,
                SourceVersion = candidate.SourceVersion,
                ReasonCode = UncredentialedInviteReason,
                AuditCorrelationId = candidate.AuditCorrelationId,
                ProjectionDigest = projectionDigest,
                CreatedAtUtc = _timeProvider.GetUtcNow(),
            });
            created++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LegacyInviteTombstoneProjectionResult(created, replayed);
    }
}

internal sealed record InstallationRecoveryPrincipalEvidence(
    string ActorId,
    long RootEpoch,
    string RootPublicKeyFingerprint);

/// <summary>Authenticates the OS-bound recovery principal; commands cannot assert their own actor.</summary>
internal interface IInstallationRecoveryPrincipalAuthority
{
    Task<InstallationRecoveryPrincipalEvidence> RequireAsync(CancellationToken cancellationToken);
}

internal sealed record RenameLegacyWebAccountCommand(
    LegacyWebAccountCompositeKey ExpectedCompositeKey,
    string NewNormalizedUsername,
    string IdempotencyKey,
    long ExpectedOwnerVersion);

internal enum RenameLegacyWebAccountStatus
{
    Renamed,
    RenamedCollisionRemaining,
    IdempotentReplay,
    CollisionNotFound,
    ImmutableSourceRefused,
    TargetUsernameCollision,
    SourceNotFound,
    SourceVersionStale,
    ChangedReplay,
}

internal sealed record RenameLegacyWebAccountResult(
    RenameLegacyWebAccountStatus Status,
    int ClassifiedCandidateCount);

/// <summary>
/// MIG-02B rename-only repair. The DB store receives no credential, membership, grant, principal,
/// Party, or link/merge mutation. Installation audit finalization is restart-safe by idempotency key.
/// </summary>
internal sealed class LegacyWebAccountRenameService
{
    private const string RenameEventType = "LegacyWebAccountRenamed";

    private readonly LegacyWebIdentityInventoryReader _inventoryReader;
    private readonly IDbBackedLegacyWebIdentityStore _databaseSource;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;
    private readonly IInstallationRecoveryPrincipalAuthority _recoveryAuthority;
    private readonly TimeProvider _timeProvider;

    internal LegacyWebAccountRenameService(
        LegacyWebIdentityInventoryReader inventoryReader,
        IDbBackedLegacyWebIdentityStore databaseSource,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        IInstallationRecoveryPrincipalAuthority recoveryAuthority,
        TimeProvider timeProvider)
    {
        _inventoryReader = inventoryReader ?? throw new ArgumentNullException(nameof(inventoryReader));
        _databaseSource = databaseSource ?? throw new ArgumentNullException(nameof(databaseSource));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _recoveryAuthority = recoveryAuthority ?? throw new ArgumentNullException(nameof(recoveryAuthority));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task<RenameLegacyWebAccountResult> ExecuteAsync(
        RenameLegacyWebAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        var normalizedTarget = LegacyWebIdentityInventoryReader.NormalizeUsername(
            command.NewNormalizedUsername);
        var commandFingerprint = InstallationAuditIntegrity.Hash(
            "rename-legacy-web-account-v1",
            command.ExpectedCompositeKey.CanonicalValue,
            normalizedTarget,
            command.IdempotencyKey,
            command.ExpectedOwnerVersion.ToString(CultureInfo.InvariantCulture));
        var idempotencyDigest = InstallationAuditIntegrity.Hash(
            "legacy-rename-idempotency-v1",
            command.IdempotencyKey);
        var auditCorrelationId = $"legacy-rename-{idempotencyDigest}";

        var replay = await ResolveAuditReplayAsync(
            auditCorrelationId,
            commandFingerprint,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var inventory = await _inventoryReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var source = inventory.Rows.SingleOrDefault(row =>
            string.Equals(
                row.CompositeKey.CanonicalValue,
                command.ExpectedCompositeKey.CanonicalValue,
                StringComparison.Ordinal));
        if (source is null)
        {
            return Result(RenameLegacyWebAccountStatus.SourceNotFound);
        }
        if (source.Mutability == LegacyWebIdentitySourceMutability.ImmutableOptionsFounder)
        {
            return Result(RenameLegacyWebAccountStatus.ImmutableSourceRefused);
        }

        var sourceKeyDigest = InstallationAuditIntegrity.Hash(
            "legacy-source-composite-key-v1",
            command.ExpectedCompositeKey.CanonicalValue);
        var targetDigest = InstallationAuditIntegrity.Hash(
            "normalized-username-v1",
            normalizedTarget);

        var pendingCollision = await FindPendingCollisionAsync(
            idempotencyDigest,
            auditCorrelationId,
            cancellationToken).ConfigureAwait(false);
        InstallationRecoveryPrincipalEvidence? recoveryEvidence = null;
        if (pendingCollision is not null)
        {
            recoveryEvidence = RecoveryEvidenceFromCheckpoint(pendingCollision);
            if (recoveryEvidence is null
                || !CheckpointMatches(
                    pendingCollision,
                    commandFingerprint,
                    sourceKeyDigest,
                    targetDigest,
                    source.SourceVersion,
                    command.ExpectedOwnerVersion))
            {
                return Result(RenameLegacyWebAccountStatus.ChangedReplay);
            }
            await ValidateRecoveryEvidenceAsync(recoveryEvidence, cancellationToken).ConfigureAwait(false);
            var recoveryResult = await TryFinalizeInterruptedRenameAsync(
                command,
                commandFingerprint,
                normalizedTarget,
                idempotencyDigest,
                auditCorrelationId,
                inventory,
                source,
                pendingCollision,
                recoveryEvidence,
                cancellationToken).ConfigureAwait(false);
            if (recoveryResult is not null)
            {
                return recoveryResult;
            }
        }
        if (source.OwnerVersion != command.ExpectedOwnerVersion)
        {
            return Result(RenameLegacyWebAccountStatus.SourceVersionStale);
        }

        var collisionRows = inventory.Rows
            .Where(row => string.Equals(
                row.NormalizedUsername,
                source.NormalizedUsername,
                StringComparison.Ordinal))
            .ToArray();
        if (collisionRows.Length <= 1)
        {
            return Result(RenameLegacyWebAccountStatus.CollisionNotFound);
        }
        if (collisionRows.Any(row =>
            row.Mutability == LegacyWebIdentitySourceMutability.ImmutableOptionsFounder))
        {
            return Result(
                RenameLegacyWebAccountStatus.ImmutableSourceRefused,
                collisionRows.Length);
        }
        if (inventory.Rows.Any(row =>
            !string.Equals(row.CompositeKey.CanonicalValue, source.CompositeKey.CanonicalValue,
                StringComparison.Ordinal)
            && string.Equals(row.NormalizedUsername, normalizedTarget, StringComparison.Ordinal)))
        {
            return Result(RenameLegacyWebAccountStatus.TargetUsernameCollision);
        }

        recoveryEvidence ??= await _recoveryAuthority.RequireAsync(cancellationToken)
            .ConfigureAwait(false);
        await ValidateRecoveryEvidenceAsync(recoveryEvidence, cancellationToken).ConfigureAwait(false);
        var collisionId = LegacyWebIdentityInventoryReader.CollisionId(source.NormalizedUsername);
        if (pendingCollision is null)
        {
            await RecordUnresolvedCollisionAsync(
                collisionId,
                collisionRows.Length,
                source.SourceVersion,
                idempotencyDigest,
                auditCorrelationId,
                commandFingerprint,
                sourceKeyDigest,
                targetDigest,
                command.ExpectedOwnerVersion,
                recoveryEvidence,
                cancellationToken).ConfigureAwait(false);
        }

        var sourceResult = await _databaseSource.RenameAsync(
            command.ExpectedCompositeKey,
            normalizedTarget,
            command.ExpectedOwnerVersion,
            command.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        switch (sourceResult.Status)
        {
            case LegacyWebIdentitySourceRenameStatus.SourceNotFound:
                await ClearRepairCheckpointAsync(
                    collisionId,
                    idempotencyDigest,
                    auditCorrelationId,
                    cancellationToken).ConfigureAwait(false);
                return Result(RenameLegacyWebAccountStatus.SourceNotFound);
            case LegacyWebIdentitySourceRenameStatus.SourceVersionStale:
                await ClearRepairCheckpointAsync(
                    collisionId,
                    idempotencyDigest,
                    auditCorrelationId,
                    cancellationToken).ConfigureAwait(false);
                return Result(RenameLegacyWebAccountStatus.SourceVersionStale);
            case LegacyWebIdentitySourceRenameStatus.TargetUsernameCollision:
                await ClearRepairCheckpointAsync(
                    collisionId,
                    idempotencyDigest,
                    auditCorrelationId,
                    cancellationToken).ConfigureAwait(false);
                return Result(RenameLegacyWebAccountStatus.TargetUsernameCollision);
            case LegacyWebIdentitySourceRenameStatus.ChangedReplay:
                await ClearRepairCheckpointAsync(
                    collisionId,
                    idempotencyDigest,
                    auditCorrelationId,
                    cancellationToken).ConfigureAwait(false);
                return Result(RenameLegacyWebAccountStatus.ChangedReplay);
            case LegacyWebIdentitySourceRenameStatus.Renamed:
            case LegacyWebIdentitySourceRenameStatus.IdempotentReplay:
                break;
            default:
                throw new InvalidOperationException("identity.legacy_rename_source_status_invalid");
        }

        var afterRename = await _inventoryReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var remaining = afterRename.Rows.Count(row =>
            string.Equals(row.NormalizedUsername, source.NormalizedUsername, StringComparison.Ordinal));
        var status = remaining > 1
            ? RenameLegacyWebAccountStatus.RenamedCollisionRemaining
            : RenameLegacyWebAccountStatus.Renamed;
        await FinalizeAuditAsync(
            collisionId,
            collisionRows.Length,
            remaining,
            idempotencyDigest,
            auditCorrelationId,
            commandFingerprint,
            command.ExpectedCompositeKey,
            normalizedTarget,
            recoveryEvidence,
            status,
            cancellationToken).ConfigureAwait(false);
        return Result(status, Math.Max(remaining, 0));
    }

    private async Task<RenameLegacyWebAccountResult?> ResolveAuditReplayAsync(
        string auditCorrelationId,
        string commandFingerprint,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var envelope = await context.AuditEnvelopes.AsNoTracking().SingleOrDefaultAsync(
            row => row.CorrelationId == auditCorrelationId,
            cancellationToken).ConfigureAwait(false);
        if (envelope is null)
        {
            return null;
        }
        var identity = await context.InstallationIdentities.AsNoTracking().SingleAsync(
            row => row.InstallationIdentityId == envelope.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        var head = await context.AuditHeads.AsNoTracking().SingleAsync(
            row => row.InstallationIdentityId == envelope.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(row => row.InstallationIdentityId == envelope.InstallationIdentityId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(envelope.EventType, RenameEventType, StringComparison.Ordinal)
            || !InstallationAuditIntegrity.HasValidChain(
                chain,
                head,
                identity.InstallationIdentityId))
        {
            throw new InvalidOperationException("identity.legacy_rename_audit_invalid");
        }
        return string.Equals(envelope.CommandFingerprint, commandFingerprint, StringComparison.Ordinal)
            ? Result(RenameLegacyWebAccountStatus.IdempotentReplay)
            : Result(RenameLegacyWebAccountStatus.ChangedReplay);
    }

    private async Task<InstallationIdentityMigrationCollisionRecord?> FindPendingCollisionAsync(
        string idempotencyDigest,
        string auditCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var matches = await context.MigrationCollisions.AsNoTracking()
            .Where(row => row.Status == InstallationIdentityCollisionStatus.Unresolved
                && row.RepairIdempotencyKeyDigest == idempotencyDigest
                && row.AuditCorrelationId == auditCorrelationId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("identity.legacy_rename_checkpoint_ambiguous"),
        };
    }

    private async Task<RenameLegacyWebAccountResult?> TryFinalizeInterruptedRenameAsync(
        RenameLegacyWebAccountCommand command,
        string commandFingerprint,
        string normalizedTarget,
        string idempotencyDigest,
        string auditCorrelationId,
        LegacyWebIdentityInventory inventory,
        LegacyWebIdentityInventoryRow source,
        InstallationIdentityMigrationCollisionRecord pendingCollision,
        InstallationRecoveryPrincipalEvidence recoveryEvidence,
        CancellationToken cancellationToken)
    {
        if (source.OwnerVersion == command.ExpectedOwnerVersion)
        {
            return null;
        }

        if (source.OwnerVersion != command.ExpectedOwnerVersion + 1
            || !string.Equals(source.NormalizedUsername, normalizedTarget, StringComparison.Ordinal)
            || inventory.Rows.Any(row =>
                !string.Equals(
                    row.CompositeKey.CanonicalValue,
                    source.CompositeKey.CanonicalValue,
                    StringComparison.Ordinal)
                && string.Equals(row.NormalizedUsername, normalizedTarget, StringComparison.Ordinal)))
        {
            return Result(RenameLegacyWebAccountStatus.ChangedReplay);
        }

        var remaining = inventory.Collisions.SingleOrDefault(collision =>
            string.Equals(
                collision.CollisionId,
                pendingCollision.CollisionId,
                StringComparison.Ordinal))?.CandidateCount ?? 1;
        var status = remaining > 1
            ? RenameLegacyWebAccountStatus.RenamedCollisionRemaining
            : RenameLegacyWebAccountStatus.Renamed;
        await FinalizeAuditAsync(
            pendingCollision.CollisionId,
            pendingCollision.CandidateCount,
            remaining,
            idempotencyDigest,
            auditCorrelationId,
            commandFingerprint,
            command.ExpectedCompositeKey,
            normalizedTarget,
            recoveryEvidence,
            status,
            cancellationToken).ConfigureAwait(false);
        return Result(status, remaining);
    }

    private async Task ValidateRecoveryEvidenceAsync(
        InstallationRecoveryPrincipalEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var identity = await context.InstallationIdentities.AsNoTracking().SingleOrDefaultAsync(
            row => row.SingletonKey == InstallationIdentityRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("identity.migration_audit_unavailable");
        var root = await context.RootKeyEpochs.AsNoTracking().SingleOrDefaultAsync(
            row => row.InstallationIdentityId == identity.InstallationIdentityId
                && row.EpochNumber == evidence.RootEpoch
                && row.Status == InstallationRootEpochStatus.Active,
            cancellationToken).ConfigureAwait(false);
        var headExists = await context.AuditHeads.AsNoTracking().AnyAsync(
            row => row.InstallationIdentityId == identity.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        if (root is null
            || identity.ActiveRootEpoch != evidence.RootEpoch
            || !string.Equals(root.RootPublicKeyFingerprint, evidence.RootPublicKeyFingerprint,
                StringComparison.Ordinal)
            || !headExists)
        {
            throw new InvalidOperationException("identity.recovery_principal_evidence_stale");
        }
    }

    private static InstallationRecoveryPrincipalEvidence? RecoveryEvidenceFromCheckpoint(
        InstallationIdentityMigrationCollisionRecord collision)
    {
        if (string.IsNullOrWhiteSpace(collision.RecoveryActorId)
            || collision.RecoveryRootEpoch is null
            || string.IsNullOrWhiteSpace(collision.RecoveryRootPublicKeyFingerprint))
        {
            return null;
        }

        return new InstallationRecoveryPrincipalEvidence(
            collision.RecoveryActorId,
            collision.RecoveryRootEpoch.Value,
            collision.RecoveryRootPublicKeyFingerprint);
    }

    private static bool CheckpointMatches(
        InstallationIdentityMigrationCollisionRecord collision,
        string commandFingerprint,
        string sourceKeyDigest,
        string targetDigest,
        long sourceVersion,
        long expectedOwnerVersion) =>
        string.Equals(collision.RepairCommandFingerprint, commandFingerprint, StringComparison.Ordinal)
        && string.Equals(collision.RepairSourceKeyDigest, sourceKeyDigest, StringComparison.Ordinal)
        && string.Equals(collision.RepairTargetDigest, targetDigest, StringComparison.Ordinal)
        && collision.ExpectedSourceVersion == sourceVersion
        && collision.ExpectedOwnerVersion == expectedOwnerVersion;

    private async Task RecordUnresolvedCollisionAsync(
        string collisionId,
        int candidateCount,
        long expectedSourceVersion,
        string idempotencyDigest,
        string auditCorrelationId,
        string commandFingerprint,
        string sourceKeyDigest,
        string targetDigest,
        long expectedOwnerVersion,
        InstallationRecoveryPrincipalEvidence recoveryEvidence,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var collision = await context.MigrationCollisions.SingleOrDefaultAsync(
            row => row.CollisionId == collisionId,
            cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (collision is null)
        {
            collision = new InstallationIdentityMigrationCollisionRecord
            {
                CollisionId = collisionId,
                CollisionKeyDigest = collisionId,
                CandidateCount = candidateCount,
                ExpectedSourceVersion = expectedSourceVersion,
                Status = InstallationIdentityCollisionStatus.Unresolved,
                RepairIdempotencyKeyDigest = idempotencyDigest,
                AuditCorrelationId = auditCorrelationId,
                RepairCommandFingerprint = commandFingerprint,
                RepairSourceKeyDigest = sourceKeyDigest,
                RepairTargetDigest = targetDigest,
                ExpectedOwnerVersion = expectedOwnerVersion,
                RecoveryActorId = recoveryEvidence.ActorId,
                RecoveryRootEpoch = recoveryEvidence.RootEpoch,
                RecoveryRootPublicKeyFingerprint = recoveryEvidence.RootPublicKeyFingerprint,
                OwnerVersion = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            context.MigrationCollisions.Add(collision);
        }
        else
        {
            var hasActiveRepair = collision.RepairIdempotencyKeyDigest is not null
                || collision.AuditCorrelationId is not null;
            if (collision.Status == InstallationIdentityCollisionStatus.Unresolved
                && hasActiveRepair
                && (!string.Equals(
                        collision.RepairIdempotencyKeyDigest,
                        idempotencyDigest,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        collision.AuditCorrelationId,
                        auditCorrelationId,
                        StringComparison.Ordinal)))
            {
                var completedRepair = collision.AuditCorrelationId is not null
                    ? await context.AuditEnvelopes.AsNoTracking().SingleOrDefaultAsync(
                        row => row.CorrelationId == collision.AuditCorrelationId,
                        cancellationToken).ConfigureAwait(false)
                    : null;
                if (completedRepair is null
                    || !CompletedRepairMatchesCheckpoint(collision, completedRepair)
                    || !await HasValidCompletedRepairAsync(
                        context,
                        completedRepair,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("identity.legacy_rename_repair_in_progress");
                }
            }

            collision.CandidateCount = candidateCount;
            collision.ExpectedSourceVersion = expectedSourceVersion;
            collision.Status = InstallationIdentityCollisionStatus.Unresolved;
            collision.RepairIdempotencyKeyDigest = idempotencyDigest;
            collision.AuditCorrelationId = auditCorrelationId;
            collision.RepairCommandFingerprint = commandFingerprint;
            collision.RepairSourceKeyDigest = sourceKeyDigest;
            collision.RepairTargetDigest = targetDigest;
            collision.ExpectedOwnerVersion = expectedOwnerVersion;
            collision.RecoveryActorId = recoveryEvidence.ActorId;
            collision.RecoveryRootEpoch = recoveryEvidence.RootEpoch;
            collision.RecoveryRootPublicKeyFingerprint = recoveryEvidence.RootPublicKeyFingerprint;
            collision.OwnerVersion++;
            collision.UpdatedAtUtc = now;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearRepairCheckpointAsync(
        string collisionId,
        string idempotencyDigest,
        string auditCorrelationId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var collision = await context.MigrationCollisions.SingleOrDefaultAsync(
            row => row.CollisionId == collisionId,
            cancellationToken).ConfigureAwait(false);
        if (collision is null
            || collision.Status != InstallationIdentityCollisionStatus.Unresolved
            || !string.Equals(
                collision.RepairIdempotencyKeyDigest,
                idempotencyDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                collision.AuditCorrelationId,
                auditCorrelationId,
                StringComparison.Ordinal))
        {
            return;
        }

        collision.RepairIdempotencyKeyDigest = null;
        collision.AuditCorrelationId = null;
        collision.RepairCommandFingerprint = null;
        collision.RepairSourceKeyDigest = null;
        collision.RepairTargetDigest = null;
        collision.ExpectedOwnerVersion = null;
        collision.RecoveryActorId = null;
        collision.RecoveryRootEpoch = null;
        collision.RecoveryRootPublicKeyFingerprint = null;
        collision.OwnerVersion++;
        collision.UpdatedAtUtc = _timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool CompletedRepairMatchesCheckpoint(
        InstallationIdentityMigrationCollisionRecord collision,
        InstallationAuditEnvelopeRecord envelope) =>
        !string.IsNullOrWhiteSpace(collision.RepairCommandFingerprint)
        && !string.IsNullOrWhiteSpace(collision.RecoveryActorId)
        && collision.RecoveryRootEpoch is not null
        && !string.IsNullOrWhiteSpace(collision.RecoveryRootPublicKeyFingerprint)
        && string.Equals(
            envelope.CommandFingerprint,
            collision.RepairCommandFingerprint,
            StringComparison.Ordinal)
        // 274: actor identities compare as actors, not as raw strings (these two are string-typed —
        // ticket 294 unifies the spaces; until then SameActor applies the same rule ActorId equality does).
        && ActorId.SameActor(envelope.ActorId, collision.RecoveryActorId)
        && envelope.RootEpoch == collision.RecoveryRootEpoch
        && string.Equals(
            envelope.RootPublicKeyFingerprint,
            collision.RecoveryRootPublicKeyFingerprint,
            StringComparison.Ordinal);

    private static async Task<bool> HasValidCompletedRepairAsync(
        NodeLocalInstallationIdentityDbContext context,
        InstallationAuditEnvelopeRecord envelope,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(envelope.EventType, RenameEventType, StringComparison.Ordinal))
        {
            return false;
        }

        var identity = await context.InstallationIdentities.AsNoTracking().SingleOrDefaultAsync(
            row => row.InstallationIdentityId == envelope.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        var head = await context.AuditHeads.AsNoTracking().SingleOrDefaultAsync(
            row => row.InstallationIdentityId == envelope.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        if (identity is null || head is null)
        {
            return false;
        }

        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(row => row.InstallationIdentityId == envelope.InstallationIdentityId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return chain.Any(row => string.Equals(
                row.CorrelationId,
                envelope.CorrelationId,
                StringComparison.Ordinal))
            && InstallationAuditIntegrity.HasValidChain(
                chain,
                head,
                identity.InstallationIdentityId);
    }

    private async Task FinalizeAuditAsync(
        string collisionId,
        int originalCandidateCount,
        int remainingCandidateCount,
        string idempotencyDigest,
        string auditCorrelationId,
        string commandFingerprint,
        LegacyWebAccountCompositeKey sourceKey,
        string normalizedTarget,
        InstallationRecoveryPrincipalEvidence recoveryEvidence,
        RenameLegacyWebAccountStatus status,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var replay = await context.AuditEnvelopes.SingleOrDefaultAsync(
            row => row.CorrelationId == auditCorrelationId,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (!string.Equals(replay.CommandFingerprint, commandFingerprint, StringComparison.Ordinal)
                || !await HasValidCompletedRepairAsync(
                    context,
                    replay,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("identity.legacy_rename_audit_changed");
            }
            return;
        }

        var identity = await context.InstallationIdentities.SingleAsync(
            row => row.SingletonKey == InstallationIdentityRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        var root = await context.RootKeyEpochs.AsNoTracking().SingleAsync(
            row => row.InstallationIdentityId == identity.InstallationIdentityId
                && row.EpochNumber == recoveryEvidence.RootEpoch
                && row.Status == InstallationRootEpochStatus.Active,
            cancellationToken).ConfigureAwait(false);
        var head = await context.AuditHeads.SingleAsync(
            row => row.InstallationIdentityId == identity.InstallationIdentityId,
            cancellationToken).ConfigureAwait(false);
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(row => row.InstallationIdentityId == identity.InstallationIdentityId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (root.EpochNumber != recoveryEvidence.RootEpoch
            || !string.Equals(
                root.RootPublicKeyFingerprint,
                recoveryEvidence.RootPublicKeyFingerprint,
                StringComparison.Ordinal)
            || !InstallationAuditIntegrity.HasValidChain(
                chain,
                head,
                identity.InstallationIdentityId))
        {
            throw new InvalidOperationException("identity.legacy_rename_audit_invalid");
        }
        var sourceKeyDigest = InstallationAuditIntegrity.Hash(
            "legacy-source-composite-key-v1",
            sourceKey.CanonicalValue);
        var targetDigest = InstallationAuditIntegrity.Hash(
            "normalized-username-v1",
            normalizedTarget);
        var payloadDigest = InstallationAuditIntegrity.Hash(
            collisionId,
            sourceKeyDigest,
            targetDigest,
            originalCandidateCount.ToString(CultureInfo.InvariantCulture),
            remainingCandidateCount.ToString(CultureInfo.InvariantCulture),
            idempotencyDigest,
            status.ToString());
        var envelope = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = identity.InstallationIdentityId,
            Sequence = head.Sequence + 1,
            CorrelationId = auditCorrelationId,
            CommandFingerprint = commandFingerprint,
            EventType = RenameEventType,
            ActorKind = "os-bound-installation-recovery-principal",
            ActorId = recoveryEvidence.ActorId,
            RootEpoch = recoveryEvidence.RootEpoch,
            RootPublicKeyFingerprint = root.RootPublicKeyFingerprint,
            PreviousHash = head.HeadHash,
            EnvelopeHash = string.Empty,
            PayloadDigest = payloadDigest,
            OccurredAtUtc = _timeProvider.GetUtcNow(),
        };
        envelope.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(envelope);
        context.AuditEnvelopes.Add(envelope);
        head.Sequence = envelope.Sequence;
        head.HeadHash = envelope.EnvelopeHash;
        head.OwnerVersion++;
        head.UpdatedAtUtc = envelope.OccurredAtUtc;

        var collision = await context.MigrationCollisions.SingleAsync(
            row => row.CollisionId == collisionId,
            cancellationToken).ConfigureAwait(false);
        collision.CandidateCount = originalCandidateCount;
        collision.Status = remainingCandidateCount > 1
            ? InstallationIdentityCollisionStatus.Unresolved
            : InstallationIdentityCollisionStatus.Renamed;
        collision.RepairIdempotencyKeyDigest = idempotencyDigest;
        collision.AuditCorrelationId = auditCorrelationId;
        collision.OwnerVersion++;
        collision.UpdatedAtUtc = envelope.OccurredAtUtc;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCommand(RenameLegacyWebAccountCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.ExpectedCompositeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey);
        if (command.IdempotencyKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Idempotency key exceeds 128 characters.");
        }
        if (command.ExpectedOwnerVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Expected owner version must be positive.");
        }
    }

    private static RenameLegacyWebAccountResult Result(
        RenameLegacyWebAccountStatus status,
        int classifiedCandidateCount = 0) =>
        new(status, classifiedCandidateCount);
}
