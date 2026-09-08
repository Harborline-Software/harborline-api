using System.Data;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The authority marker that is currently safe to resolve for this installation.</summary>
public enum InstallationIdentityAuthorityKind
{
    LegacyV1,
    Revision3V2,
}

/// <summary>A restart-stable read of the installation authority marker.</summary>
public sealed record InstallationIdentityAuthorityResolution(
    bool IsReadable,
    InstallationIdentityCutoverStage Stage,
    InstallationIdentityAuthorityKind? Authority,
    string? RefusalCode);

/// <summary>An opaque lease capability. Every transition revalidates all four fields in-store.</summary>
public sealed record InstallationIdentityMigrationLease(
    string LeaseId,
    string HolderFingerprint,
    long Generation,
    DateTimeOffset ExpiresAtUtc);

public enum InstallationIdentityLeaseAcquireStatus
{
    Acquired,
    AlreadyHeld,
    HeldByAnother,
}

/// <summary>Result of an exclusive migration-lease acquisition.</summary>
public sealed record InstallationIdentityLeaseAcquireResult(
    InstallationIdentityLeaseAcquireStatus Status,
    InstallationIdentityMigrationLease? Lease);

public enum InstallationIdentityLeaseRenewStatus
{
    Renewed,
    LeaseNotHeld,
}

/// <summary>Result of extending a live migration lease without rotating its identity.</summary>
public sealed record InstallationIdentityLeaseRenewResult(
    InstallationIdentityLeaseRenewStatus Status,
    InstallationIdentityMigrationLease? Lease);

/// <summary>Evidence supplied by the copy/verifier collaborators before a stage can advance.</summary>
public sealed record InstallationIdentityCutoverEvidence(
    string MigrationRunId,
    string? SourceWatermarkDigest = null,
    string? FinalVerificationDigest = null);

public enum InstallationIdentityCutoverAdvanceStatus
{
    Advanced,
    AlreadyAtStage,
    LeaseNotHeld,
    InvalidStageTransition,
    InvariantRefused,
}

/// <summary>Non-secret outcome from one compare-and-advance attempt.</summary>
public sealed record InstallationIdentityCutoverAdvanceResult(
    InstallationIdentityCutoverAdvanceStatus Status,
    InstallationIdentityCutoverStage Stage,
    string? RefusalCode);

/// <summary>The four purpose-bound legacy bearer audiences retired by the v2 marker.</summary>
public enum InstallationIdentityLegacyBearerAudience
{
    AccountChallenge,
    SelectedUser,
    InstallationSession,
    Tooling,
}

public enum InstallationIdentityLegacyBearerRevocationStatus
{
    Staged,
    AlreadyStaged,
    LeaseNotHeld,
    InvalidStage,
    InvariantRefused,
}

/// <summary>Outcome from staging the exact legacy bearer-audience set.</summary>
public sealed record InstallationIdentityLegacyBearerRevocationResult(
    InstallationIdentityLegacyBearerRevocationStatus Status,
    string? RefusalCode);

/// <summary>Admission result consumed by legacy-v1 mutation owners while the barrier is raised.</summary>
public sealed record InstallationIdentityV1MutationAdmission(bool IsAllowed, string? RefusalCode);

/// <summary>Runtime gate consumed by every legacy credential/session accept path.</summary>
public interface IInstallationIdentityV1AuthorityGate
{
    Task<InstallationIdentityV1MutationAdmission> CheckV1MutationAdmissionAsync(
        CancellationToken cancellationToken = default);

    Task<InstallationIdentityV1MutationAdmission> CheckLegacyBearerAdmissionAsync(
        InstallationIdentityLegacyBearerAudience audience,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the restart-safe v1-to-v2 installation cutover marker. No route or hosted worker is added:
/// copy, verification, and legacy-writer integrations remain explicit collaborators of later cards.
/// </summary>
public sealed class InstallationIdentityCutoverOrchestrator : IInstallationIdentityV1AuthorityGate
{
    public const string MigrationInProgressRefusal = "identity_migration_in_progress";
    public const string LegacyAuthorityRetiredRefusal = "identity_v1_authority_retired";

    private const int BusyRetryCount = 8;
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    private readonly IInstallationAuthorityVersionRegistry _versions;

    /// <summary>
    /// Fail-closed convenience composition. The default registry gives V2 no sign-in paths, so it
    /// reports NOT ready — the true answer today, and the safe answer if this overload is ever
    /// reached from a composition root that meant to supply its own.
    /// </summary>
    public InstallationIdentityCutoverOrchestrator(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider)
        : this(contextFactory, timeProvider, InstallationAuthorityVersionRegistry.CreateDefault())
    {
    }

    public InstallationIdentityCutoverOrchestrator(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider,
        IInstallationAuthorityVersionRegistry versions)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
    }

    /// <summary>Maps a durable stage to the authority version in force at that stage.</summary>
    /// <remarks>
    /// Only <see cref="InstallationIdentityCutoverStage.V2Authoritative"/> is v2. Every earlier
    /// stage — including the write barrier and the copy — still has v1 as the authority, because
    /// the marker has not committed. Keeping this mapping in ONE place is what lets callers stop
    /// naming versions.
    /// </remarks>
    private static InstallationAuthorityVersion VersionOf(InstallationIdentityCutoverStage stage) =>
        stage switch
        {
            InstallationIdentityCutoverStage.LegacyV1Authoritative => InstallationAuthorityVersion.V1,
            InstallationIdentityCutoverStage.WriteBarrierActive => InstallationAuthorityVersion.V1,
            InstallationIdentityCutoverStage.Copying => InstallationAuthorityVersion.V1,
            InstallationIdentityCutoverStage.Verified => InstallationAuthorityVersion.V1,
            InstallationIdentityCutoverStage.V2Authoritative => InstallationAuthorityVersion.V2,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

    /// <summary>
    /// Acquires the singleton lease. A live holder wins; an expired or released holder is replaced
    /// with a new lease id and a strictly greater generation so stale capabilities cannot resume.
    /// </summary>
    public async Task<InstallationIdentityLeaseAcquireResult> AcquireLeaseAsync(
        string holderFingerprint,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateHolderFingerprint(holderFingerprint);
        ValidateLeaseDuration(duration);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryAcquireLeaseAsync(holderFingerprint, duration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Extends a live lease for a long-running stage. Renewal preserves the holder, generation,
    /// and lease id; the returned expiry becomes part of the replacement capability.
    /// </summary>
    public async Task<InstallationIdentityLeaseRenewResult> RenewLeaseAsync(
        InstallationIdentityMigrationLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseDuration(duration);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryRenewLeaseAsync(lease, duration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Advances exactly one stage after evaluating the full gate chain: live lease, valid current
    /// marker, durable write barrier, stage evidence, and a readable authority after the mutation.
    /// </summary>
    public async Task<InstallationIdentityCutoverAdvanceResult> AdvanceAsync(
        InstallationIdentityMigrationLease lease,
        InstallationIdentityCutoverStage targetStage,
        InstallationIdentityCutoverEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateEvidence(evidence);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryAdvanceAsync(lease, targetStage, evidence, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Stages one canonical digest proving that every legacy bearer audience is retired. The
    /// marker CAS refuses until this evidence is durable and bound to the verified migration run.
    /// </summary>
    public async Task<InstallationIdentityLegacyBearerRevocationResult>
        StageLegacyBearerRevocationsAsync(
            InstallationIdentityMigrationLease lease,
            IReadOnlyCollection<InstallationIdentityLegacyBearerAudience> audiences,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(audiences);

        var canonicalAudiences = CanonicalLegacyBearerAudiences();
        var suppliedAudiences = audiences
            .Distinct()
            .Order()
            .ToArray();
        if (!suppliedAudiences.SequenceEqual(canonicalAudiences))
        {
            return new InstallationIdentityLegacyBearerRevocationResult(
                InstallationIdentityLegacyBearerRevocationStatus.InvariantRefused,
                "legacy_bearer_audience_set_incomplete");
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryStageLegacyBearerRevocationsAsync(
                        lease,
                        canonicalAudiences,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(5 * (attempt + 1)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<InstallationIdentityCutoverAdvanceResult> TryAdvanceAsync(
        InstallationIdentityMigrationLease lease,
        InstallationIdentityCutoverStage targetStage,
        InstallationIdentityCutoverEvidence evidence,
        CancellationToken cancellationToken)
    {

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var storedLease = await context.MigrationLeases.SingleOrDefaultAsync(
            row => row.SingletonKey == InstallationIdentityMigrationLeaseRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        var state = await context.CutoverStates.SingleAsync(
            row => row.SingletonKey == InstallationIdentityCutoverStateRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);

        if (!LeaseIsHeld(storedLease, lease, now))
        {
            return Refused(
                InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld,
                state.Stage,
                "migration_lease_not_held");
        }

        var currentAuthority = await ResolveAuthorityAsync(context, state, cancellationToken)
            .ConfigureAwait(false);
        if (!currentAuthority.IsReadable)
        {
            return Refused(
                InstallationIdentityCutoverAdvanceStatus.InvariantRefused,
                state.Stage,
                currentAuthority.RefusalCode ?? "authority_marker_unreadable");
        }

        if (state.Stage == targetStage)
        {
            return Refused(InstallationIdentityCutoverAdvanceStatus.AlreadyAtStage, state.Stage, null);
        }

        if (!IsNextStage(state.Stage, targetStage))
        {
            return Refused(
                InstallationIdentityCutoverAdvanceStatus.InvalidStageTransition,
                state.Stage,
                "cutover_stage_not_next");
        }

        var invariantRefusal = await ApplyTargetStageAsync(
            context,
            state,
            targetStage,
            evidence,
            now,
            cancellationToken).ConfigureAwait(false);
        if (invariantRefusal is not null)
        {
            return Refused(
                InstallationIdentityCutoverAdvanceStatus.InvariantRefused,
                state.Stage,
                invariantRefusal);
        }

        // No-bricking guard: the old marker remains readable until the atomic v2 flip, and the v2
        // marker is proven readable before that transaction is allowed to commit.
        var targetAuthority = await EnsureNoBrickingAuthorityReadableAsync(context, state, cancellationToken)
            .ConfigureAwait(false);
        if (!targetAuthority.IsReadable)
        {
            return Refused(
                InstallationIdentityCutoverAdvanceStatus.InvariantRefused,
                state.Stage,
                targetAuthority.RefusalCode ?? "authority_marker_unreadable");
        }

        if (state.Stage == InstallationIdentityCutoverStage.V2Authoritative)
        {
            ReleaseLeaseOnCleanCompletion(storedLease!, now);
        }

        state.OwnerVersion++;
        state.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new InstallationIdentityCutoverAdvanceResult(
            InstallationIdentityCutoverAdvanceStatus.Advanced,
            state.Stage,
            null);
    }

    private async Task<InstallationIdentityLegacyBearerRevocationResult>
        TryStageLegacyBearerRevocationsAsync(
            InstallationIdentityMigrationLease lease,
            IReadOnlyList<InstallationIdentityLegacyBearerAudience> audiences,
            CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var storedLease = await context.MigrationLeases.SingleOrDefaultAsync(
            row => row.SingletonKey == InstallationIdentityMigrationLeaseRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        var state = await context.CutoverStates.SingleAsync(
            row => row.SingletonKey == InstallationIdentityCutoverStateRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);

        if (!LeaseIsHeld(storedLease, lease, now))
        {
            return new InstallationIdentityLegacyBearerRevocationResult(
                InstallationIdentityLegacyBearerRevocationStatus.LeaseNotHeld,
                "migration_lease_not_held");
        }
        if (state.Stage != InstallationIdentityCutoverStage.Verified)
        {
            return new InstallationIdentityLegacyBearerRevocationResult(
                InstallationIdentityLegacyBearerRevocationStatus.InvalidStage,
                "legacy_bearer_revocation_requires_verified");
        }

        var authority = await ResolveAuthorityAsync(context, state, cancellationToken)
            .ConfigureAwait(false);
        if (!authority.IsReadable ||
            authority.Authority != InstallationIdentityAuthorityKind.LegacyV1)
        {
            return new InstallationIdentityLegacyBearerRevocationResult(
                InstallationIdentityLegacyBearerRevocationStatus.InvariantRefused,
                authority.RefusalCode ?? "legacy_v1_authority_marker_invalid");
        }

        var digest = ComputeLegacyBearerRevocationDigest(state, audiences);
        if (state.LegacyBearerRevocationDigest is not null)
        {
            return new InstallationIdentityLegacyBearerRevocationResult(
                string.Equals(
                    state.LegacyBearerRevocationDigest,
                    digest,
                    StringComparison.Ordinal)
                    ? InstallationIdentityLegacyBearerRevocationStatus.AlreadyStaged
                    : InstallationIdentityLegacyBearerRevocationStatus.InvariantRefused,
                string.Equals(
                    state.LegacyBearerRevocationDigest,
                    digest,
                    StringComparison.Ordinal)
                    ? null
                    : "legacy_bearer_revocation_evidence_changed");
        }

        state.LegacyBearerRevocationDigest = digest;
        state.OwnerVersion++;
        state.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InstallationIdentityLegacyBearerRevocationResult(
            InstallationIdentityLegacyBearerRevocationStatus.Staged,
            null);
    }

    /// <summary>Reads the durable marker without acquiring the migration lease.</summary>
    public async Task<InstallationIdentityAuthorityResolution> ResolveAuthorityAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var state = await context.CutoverStates.AsNoTracking().SingleAsync(
            row => row.SingletonKey == InstallationIdentityCutoverStateRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        return await ResolveAuthorityAsync(context, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fail-closed legacy mutation gate. Later writer cards consume this seam; this card owns the
    /// durable barrier state and its canonical refusal only.
    /// </summary>
    public async Task<InstallationIdentityV1MutationAdmission> CheckV1MutationAdmissionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var state = await context.CutoverStates.AsNoTracking().SingleAsync(
            row => row.SingletonKey == InstallationIdentityCutoverStateRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        var authority = await ResolveAuthorityAsync(context, state, cancellationToken).ConfigureAwait(false);

        // Authority readability is version-independent. The selected policy owns every condition
        // that names a version-specific stage or barrier so successor refusal cannot be bypassed.
        var stageAdmits = authority.IsReadable;
        var policy = _versions.Get(VersionOf(state.Stage));
        var allowed = stageAdmits && await policy.IsAdmissionAllowedAsync(
            state.Stage,
            state.V1WriteBarrierVersion,
            InstallationCutoverAdmissionKind.V1Mutation,
            null,
            cancellationToken)
            .ConfigureAwait(false);
        return allowed
            ? new InstallationIdentityV1MutationAdmission(true, null)
            : new InstallationIdentityV1MutationAdmission(false, RefusalFor(state, policy));
    }

    /// <summary>
    /// Rejects every purpose-bound legacy bearer after the barrier rises. After the marker CAS the
    /// refusal changes from migration-in-progress to permanently retired; no downgrade can make a
    /// physically surviving v1 token authoritative again.
    /// </summary>
    public async Task<InstallationIdentityV1MutationAdmission> CheckLegacyBearerAdmissionAsync(
        InstallationIdentityLegacyBearerAudience audience,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(audience))
        {
            throw new ArgumentOutOfRangeException(nameof(audience));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var state = await context.CutoverStates.AsNoTracking().SingleAsync(
            row => row.SingletonKey == InstallationIdentityCutoverStateRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        var authority = await ResolveAuthorityAsync(context, state, cancellationToken)
            .ConfigureAwait(false);
        var stageAdmits = authority.IsReadable;
        var policy = _versions.Get(VersionOf(state.Stage));
        var allowed = stageAdmits && await policy.IsAdmissionAllowedAsync(
            state.Stage,
            state.V1WriteBarrierVersion,
            InstallationCutoverAdmissionKind.LegacyBearer,
            audience,
            cancellationToken)
            .ConfigureAwait(false);
        if (allowed)
        {
            return new InstallationIdentityV1MutationAdmission(true, null);
        }
        return new InstallationIdentityV1MutationAdmission(false, RefusalFor(state, policy));
    }

    private async Task<InstallationIdentityLeaseAcquireResult> TryAcquireLeaseAsync(
        string holderFingerprint,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var existing = await context.MigrationLeases.SingleOrDefaultAsync(
            row => row.SingletonKey == InstallationIdentityMigrationLeaseRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null && existing.ReleasedAtUtc is null && existing.ExpiresAtUtc > now)
        {
            var held = ToLease(existing);
            return string.Equals(existing.HolderFingerprint, holderFingerprint, StringComparison.Ordinal)
                ? new InstallationIdentityLeaseAcquireResult(
                    InstallationIdentityLeaseAcquireStatus.AlreadyHeld,
                    held)
                : new InstallationIdentityLeaseAcquireResult(
                    InstallationIdentityLeaseAcquireStatus.HeldByAnother,
                    null);
        }

        var expiresAtUtc = now.Add(duration);
        if (existing is null)
        {
            existing = new InstallationIdentityMigrationLeaseRecord
            {
                SingletonKey = InstallationIdentityMigrationLeaseRecord.SingletonKeyValue,
                LeaseId = RandomHex(32),
                HolderFingerprint = holderFingerprint,
                LeaseGeneration = 1,
                OwnerVersion = 1,
                AcquiredAtUtc = now,
                ExpiresAtUtc = expiresAtUtc,
                ReleasedAtUtc = null,
            };
            context.MigrationLeases.Add(existing);
        }
        else
        {
            existing.LeaseId = RandomHex(32);
            existing.HolderFingerprint = holderFingerprint;
            existing.LeaseGeneration++;
            existing.OwnerVersion++;
            existing.AcquiredAtUtc = now;
            existing.ExpiresAtUtc = expiresAtUtc;
            existing.ReleasedAtUtc = null;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InstallationIdentityLeaseAcquireResult(
            InstallationIdentityLeaseAcquireStatus.Acquired,
            ToLease(existing));
    }

    private async Task<InstallationIdentityLeaseRenewResult> TryRenewLeaseAsync(
        InstallationIdentityMigrationLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var storedLease = await context.MigrationLeases.SingleOrDefaultAsync(
            row => row.SingletonKey == InstallationIdentityMigrationLeaseRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);

        if (!LeaseIsHeld(storedLease, lease, now))
        {
            return new InstallationIdentityLeaseRenewResult(
                InstallationIdentityLeaseRenewStatus.LeaseNotHeld,
                null);
        }

        var requestedExpiry = now.Add(duration);
        if (requestedExpiry > storedLease!.ExpiresAtUtc)
        {
            storedLease.ExpiresAtUtc = requestedExpiry;
            storedLease.OwnerVersion++;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new InstallationIdentityLeaseRenewResult(
            InstallationIdentityLeaseRenewStatus.Renewed,
            ToLease(storedLease));
    }

    private async Task<string?> ApplyTargetStageAsync(
        NodeLocalInstallationIdentityDbContext context,
        InstallationIdentityCutoverStateRecord state,
        InstallationIdentityCutoverStage targetStage,
        InstallationIdentityCutoverEvidence evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (state.Stage != InstallationIdentityCutoverStage.LegacyV1Authoritative &&
            !string.Equals(state.MigrationRunId, evidence.MigrationRunId, StringComparison.Ordinal))
        {
            return "migration_run_mismatch";
        }

        switch (targetStage)
        {
            case InstallationIdentityCutoverStage.WriteBarrierActive:
                state.Stage = targetStage;
                state.MigrationRunId = evidence.MigrationRunId;
                state.V1WriteBarrierVersion++;
                return null;

            case InstallationIdentityCutoverStage.Copying:
                if (state.V1WriteBarrierVersion <= 0)
                {
                    return "v1_write_barrier_inactive";
                }

                state.Stage = targetStage;
                return null;

            case InstallationIdentityCutoverStage.Verified:
                if (state.V1WriteBarrierVersion <= 0)
                {
                    return "v1_write_barrier_inactive";
                }

                if (string.IsNullOrWhiteSpace(evidence.SourceWatermarkDigest) ||
                    string.IsNullOrWhiteSpace(evidence.FinalVerificationDigest))
                {
                    return "cutover_verification_evidence_missing";
                }

                var watermarkExists = await context.MigrationSourceWatermarks.AsNoTracking().AnyAsync(
                    row => row.MigrationRunId == evidence.MigrationRunId &&
                        row.SnapshotDigest == evidence.SourceWatermarkDigest,
                    cancellationToken).ConfigureAwait(false);
                if (!watermarkExists)
                {
                    return "source_watermark_unverified";
                }

                var hasUnresolvedCollision = await context.MigrationCollisions.AsNoTracking().AnyAsync(
                    row => row.Status == InstallationIdentityCollisionStatus.Unresolved,
                    cancellationToken).ConfigureAwait(false);
                if (hasUnresolvedCollision)
                {
                    return "identity_install_scope_collision";
                }

                if (!await HasReadableV2CandidateAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    return "identity_install_root_unresolved";
                }

                state.Stage = targetStage;
                state.SourceWatermarkDigest = evidence.SourceWatermarkDigest;
                state.FinalVerificationDigest = evidence.FinalVerificationDigest;
                return null;

            case InstallationIdentityCutoverStage.V2Authoritative:
                if (state.V1WriteBarrierVersion <= 0)
                {
                    return "v1_write_barrier_inactive";
                }

                // The successor answers for its own readiness. This used to be an inline data-substrate
                // check that could not see whether anything could still serve sign-in afterwards; the
                // policy CAN, because it is a registered object and takes its collaborators by
                // injection. A refusal here means the successor said it is not ready, whatever its
                // reasons are — the stage machine deliberately does not know them.
                if (!await _versions.Get(VersionOf(targetStage))
                        .IsReadyAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    return "successor_not_ready";
                }

                if (string.IsNullOrWhiteSpace(state.SourceWatermarkDigest) ||
                    string.IsNullOrWhiteSpace(state.FinalVerificationDigest))
                {
                    return "final_verification_not_readable";
                }
                if (!HasExactLegacyBearerRevocationEvidence(state))
                {
                    return "legacy_bearer_revocation_not_staged";
                }

                state.Stage = targetStage;
                state.AuthorityVersion = InstallationIdentityCutoverStateRecord.Revision3V2AuthorityVersion;
                state.CommittedAtUtc = now;
                return null;

            default:
                return "cutover_target_invalid";
        }
    }

    private async Task<InstallationIdentityAuthorityResolution> EnsureNoBrickingAuthorityReadableAsync(
        NodeLocalInstallationIdentityDbContext context,
        InstallationIdentityCutoverStateRecord state,
        CancellationToken cancellationToken) =>
        await ResolveAuthorityAsync(context, state, cancellationToken).ConfigureAwait(false);

    private async Task<InstallationIdentityAuthorityResolution> ResolveAuthorityAsync(
        NodeLocalInstallationIdentityDbContext context,
        InstallationIdentityCutoverStateRecord state,
        CancellationToken cancellationToken)
    {
        if (state.Stage == InstallationIdentityCutoverStage.LegacyV1Authoritative)
        {
            var readable = state.AuthorityVersion == InstallationIdentityCutoverStateRecord.LegacyV1AuthorityVersion &&
                state.V1WriteBarrierVersion == 0 &&
                state.CommittedAtUtc is null;
            return Resolution(
                readable,
                state,
                InstallationIdentityAuthorityKind.LegacyV1,
                "legacy_v1_authority_marker_invalid");
        }

        if (state.Stage is InstallationIdentityCutoverStage.WriteBarrierActive or
            InstallationIdentityCutoverStage.Copying or
            InstallationIdentityCutoverStage.Verified)
        {
            var verifiedEvidencePresent = state.Stage != InstallationIdentityCutoverStage.Verified ||
                (!string.IsNullOrWhiteSpace(state.SourceWatermarkDigest) &&
                    !string.IsNullOrWhiteSpace(state.FinalVerificationDigest));
            var readable = state.AuthorityVersion == InstallationIdentityCutoverStateRecord.LegacyV1AuthorityVersion &&
                state.V1WriteBarrierVersion > 0 &&
                !string.IsNullOrWhiteSpace(state.MigrationRunId) &&
                verifiedEvidencePresent &&
                state.CommittedAtUtc is null;
            return Resolution(
                readable,
                state,
                InstallationIdentityAuthorityKind.LegacyV1,
                readable ? null : "legacy_v1_authority_marker_invalid");
        }

        if (state.Stage == InstallationIdentityCutoverStage.V2Authoritative)
        {
            // Deliberately use the committed data check directly, not policy.IsReadyAsync. Readiness
            // is a pre-commit gate that also requires a registered sign-in path. Requiring that path
            // again while resolving a committed marker would make the installation unreadable after
            // a restart where the registration is absent -- the brick this cutover guard prevents.
            var readable = state.AuthorityVersion ==
                    InstallationIdentityCutoverStateRecord.Revision3V2AuthorityVersion &&
                state.V1WriteBarrierVersion > 0 &&
                !string.IsNullOrWhiteSpace(state.FinalVerificationDigest) &&
                state.CommittedAtUtc is not null &&
                HasExactLegacyBearerRevocationEvidence(state) &&
                await HasReadableV2CandidateAsync(context, cancellationToken).ConfigureAwait(false);
            return Resolution(
                readable,
                state,
                InstallationIdentityAuthorityKind.Revision3V2,
                readable ? null : "revision3_v2_authority_marker_invalid");
        }

        return Resolution(false, state, null, "cutover_stage_unknown");
    }

    /// <summary>
    /// Which refusal a rejected caller hears. Retirement is the POLICY's word -- once the successor
    /// is authoritative it names the code -- and anything earlier is the migration still running.
    /// Previously this ternary was inlined at one of the two call sites and absent at the other, so
    /// a refused v1 mutation after the flip reported "migration in progress" when the migration had
    /// finished and the authority was gone.
    /// </summary>
    private static string RefusalFor(
        InstallationIdentityCutoverStateRecord state,
        IInstallationAuthorityVersionPolicy policy)
    {
        if (state.Stage != InstallationIdentityCutoverStage.V2Authoritative)
        {
            return MigrationInProgressRefusal;
        }

        return policy.RetirementRefusalCode ?? throw new InvalidOperationException(
            $"Authority version {policy.Version} did not name its retirement refusal.");
    }

    /// <summary>
    /// Whether the v2 DATA substrate is ready: a verified root designation, an identity row at the
    /// Revision 3 authority version, and an active account plus active installation grant for the
    /// designated account.
    /// </summary>
    /// <remarks>
    /// <b>Read the name narrowly.</b> This says the v2 ROWS are in place. It does NOT say a v2
    /// sign-in path exists, and it must not be relied on for that. Committing the authority-version
    /// marker makes this installation reject every v1 cookie audience permanently (ADR 0160 R3-H),
    /// and today the only registered <c>IWebAccountAccessChallengeIssuer</c> and
    /// <c>IWebTenantSelectionAuthority</c> are the v1 implementations with no replacement behind
    /// them — so a flip that satisfied every check here would still leave the installation unable to
    /// sign anyone in.
    ///
    /// The v2 policy composes this data predicate with its injected sign-in paths in
    /// <see cref="V2InstallationAuthorityVersionPolicy.IsReadyAsync"/>. Stage advancement consults
    /// that policy before committing the marker, so the cutover is fenced on both data and a usable
    /// successor sign-in path without making this narrow predicate claim more than it can observe.
    /// </remarks>
    internal static async Task<bool> HasReadableV2CandidateAsync(
        NodeLocalInstallationIdentityDbContext context,
        CancellationToken cancellationToken)
    {
        var designation = await context.RootDesignations.AsNoTracking().SingleOrDefaultAsync(
            row => row.SingletonKey == InstallationIdentityRootDesignationRecord.SingletonKeyValue,
            cancellationToken).ConfigureAwait(false);
        if (designation?.AccountId is null || designation.VerifiedAtUtc is null)
        {
            return false;
        }

        var identityReady = await context.InstallationIdentities.AsNoTracking().AnyAsync(
            row => row.SingletonKey == InstallationIdentityRecord.SingletonKeyValue &&
                row.AuthorityVersion == InstallationIdentityRecord.Revision3AuthorityVersion,
            cancellationToken).ConfigureAwait(false);
        var accountReady = await context.Accounts.AsNoTracking().AnyAsync(
            row => row.AccountId == designation.AccountId && row.Status == InstallationAccountStatus.Active,
            cancellationToken).ConfigureAwait(false);
        var grantReady = await context.InstallationAccessGrants.AsNoTracking().AnyAsync(
            row => row.AccountId == designation.AccountId &&
                row.Status == InstallationAccessGrantStatus.Active,
            cancellationToken).ConfigureAwait(false);
        return identityReady && accountReady && grantReady;
    }

    private bool HasExactLegacyBearerRevocationEvidence(
        InstallationIdentityCutoverStateRecord state) =>
        !string.IsNullOrWhiteSpace(state.LegacyBearerRevocationDigest) &&
        string.Equals(
            state.LegacyBearerRevocationDigest,
            ComputeLegacyBearerRevocationDigest(state, CanonicalLegacyBearerAudiences()),
            StringComparison.Ordinal);

    /// <summary>
    /// The audience set the SUCCESSOR retires, asked of the successor rather than assumed.
    ///
    /// This used to enumerate the audience enum wholesale, which is a v1-shaped assumption baked
    /// into the stage machine: it silently means "every audience that has ever existed is retired by
    /// whatever comes next". A v3 retiring a different subset would have been wrong here with
    /// nothing to catch it, because the digest would still have computed cleanly over the wrong set.
    /// </summary>
    private InstallationIdentityLegacyBearerAudience[] CanonicalLegacyBearerAudiences() =>
        _versions.Get(InstallationAuthorityVersion.V2).RetiredAudiences.ToArray();

    private static string ComputeLegacyBearerRevocationDigest(
        InstallationIdentityCutoverStateRecord state,
        IEnumerable<InstallationIdentityLegacyBearerAudience> audiences)
    {
        if (string.IsNullOrWhiteSpace(state.MigrationRunId) ||
            string.IsNullOrWhiteSpace(state.FinalVerificationDigest))
        {
            throw new InvalidOperationException(
                "identity.legacy_bearer_revocation_evidence_unverified");
        }
        return InstallationAuditIntegrity.Hash(
            [
                "legacy-v1-bearer-revocation/v1",
                state.MigrationRunId,
                state.FinalVerificationDigest,
                .. audiences.Order().Select(audience => audience.ToString()),
            ]);
    }

    private static InstallationIdentityAuthorityResolution Resolution(
        bool readable,
        InstallationIdentityCutoverStateRecord state,
        InstallationIdentityAuthorityKind? authority,
        string? refusalCode) =>
        new(readable, state.Stage, readable ? authority : null, readable ? null : refusalCode);

    private static InstallationIdentityCutoverAdvanceResult Refused(
        InstallationIdentityCutoverAdvanceStatus status,
        InstallationIdentityCutoverStage stage,
        string? code) => new(status, stage, code);

    private static bool IsNextStage(
        InstallationIdentityCutoverStage current,
        InstallationIdentityCutoverStage target) =>
        (current, target) switch
        {
            (InstallationIdentityCutoverStage.LegacyV1Authoritative,
                InstallationIdentityCutoverStage.WriteBarrierActive) => true,
            (InstallationIdentityCutoverStage.WriteBarrierActive,
                InstallationIdentityCutoverStage.Copying) => true,
            (InstallationIdentityCutoverStage.Copying,
                InstallationIdentityCutoverStage.Verified) => true,
            (InstallationIdentityCutoverStage.Verified,
                InstallationIdentityCutoverStage.V2Authoritative) => true,
            _ => false,
        };

    private static bool LeaseIsHeld(
        InstallationIdentityMigrationLeaseRecord? stored,
        InstallationIdentityMigrationLease supplied,
        DateTimeOffset now) =>
        stored is not null &&
        stored.ReleasedAtUtc is null &&
        stored.ExpiresAtUtc > now &&
        stored.LeaseGeneration == supplied.Generation &&
        stored.ExpiresAtUtc == supplied.ExpiresAtUtc &&
        string.Equals(stored.LeaseId, supplied.LeaseId, StringComparison.Ordinal) &&
        string.Equals(stored.HolderFingerprint, supplied.HolderFingerprint, StringComparison.Ordinal);

    private static InstallationIdentityMigrationLease ToLease(
        InstallationIdentityMigrationLeaseRecord record) =>
        new(record.LeaseId, record.HolderFingerprint, record.LeaseGeneration, record.ExpiresAtUtc);

    private static void ReleaseLeaseOnCleanCompletion(
        InstallationIdentityMigrationLeaseRecord storedLease,
        DateTimeOffset now)
    {
        storedLease.ReleasedAtUtc = now;
        storedLease.OwnerVersion++;
    }

    private static void ValidateHolderFingerprint(string holderFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holderFingerprint);
        if (holderFingerprint.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(holderFingerprint));
        }
    }

    private static void ValidateLeaseDuration(TimeSpan duration)
    {
        if (duration < MinimumLeaseDuration || duration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                $"Lease duration must be between {MinimumLeaseDuration} and {MaximumLeaseDuration}.");
        }
    }

    private static void ValidateEvidence(InstallationIdentityCutoverEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.MigrationRunId);
        if (evidence.MigrationRunId.Length > 64 ||
            evidence.SourceWatermarkDigest?.Length > 128 ||
            evidence.FinalVerificationDigest?.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }
    }

    private static bool IsRetryableContention(Exception exception) =>
        exception is DbUpdateConcurrencyException or
            SqliteException { SqliteErrorCode: 5 or 6 } ||
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 or 19 };

    private static string RandomHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount));
}
