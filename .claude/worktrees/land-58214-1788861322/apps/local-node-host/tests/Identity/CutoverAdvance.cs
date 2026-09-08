using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Drives a REAL <see cref="InstallationIdentityCutoverOrchestrator"/> all the way to a committed v2
/// authority marker over a caller-supplied installation identity store.
/// </summary>
/// <remarks>
/// Shared so a production accept path can be proved against genuine post-cutover state rather than a
/// stubbed gate: the caller keeps its own real authority (challenge issuer, tenant selection, …)
/// bound to the same orchestrator, and every step below is asserted, so a proof that fails to reach
/// <c>V2Authoritative</c> says which stage refused instead of silently testing the pre-cutover state.
/// The store must already carry a bootstrapped founder — the marker CAS re-verifies the initial
/// installation-root designation and refuses with <c>identity_install_root_unresolved</c> without it.
/// </remarks>
internal static class CutoverAdvance
{
    private const string MigrationRunId = "mtw-3245-accept-path-run";
    private const string WatermarkDigest = "mtw-3245-watermark-digest";
    private const string VerificationDigest = "mtw-3245-verification-digest";

    /// <summary>Seeds the source watermark, then leases and advances through to the committed marker.</summary>
    internal static async Task CommitV2MarkerAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        InstallationIdentityCutoverOrchestrator orchestrator,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identityFactory);
        ArgumentNullException.ThrowIfNull(orchestrator);

        await SeedSourceWatermarkAsync(identityFactory, now).ConfigureAwait(false);

        var acquired = await orchestrator
            .AcquireLeaseAsync("mtw-3245-coordinator", TimeSpan.FromHours(1))
            .ConfigureAwait(false);
        Require(
            acquired.Status == InstallationIdentityLeaseAcquireStatus.Acquired && acquired.Lease is not null,
            $"the migration lease could not be acquired ('{acquired.Status}').");
        var lease = acquired.Lease!;

        foreach (var stage in new[]
        {
            InstallationIdentityCutoverStage.WriteBarrierActive,
            InstallationIdentityCutoverStage.Copying,
            InstallationIdentityCutoverStage.Verified,
        })
        {
            var advanced = await orchestrator.AdvanceAsync(lease, stage, Evidence()).ConfigureAwait(false);
            Require(
                advanced.Status == InstallationIdentityCutoverAdvanceStatus.Advanced,
                $"the cutover could not advance to '{stage}' ('{advanced.RefusalCode}').");
        }

        var staged = await orchestrator
            .StageLegacyBearerRevocationsAsync(lease, Enum.GetValues<InstallationIdentityLegacyBearerAudience>())
            .ConfigureAwait(false);
        Require(
            staged.Status is InstallationIdentityLegacyBearerRevocationStatus.Staged
                or InstallationIdentityLegacyBearerRevocationStatus.AlreadyStaged,
            $"the legacy bearer revocation evidence was not staged ('{staged.Status}' / '{staged.RefusalCode}').");

        var committed = await orchestrator
            .AdvanceAsync(lease, InstallationIdentityCutoverStage.V2Authoritative, Evidence())
            .ConfigureAwait(false);
        Require(
            committed.Status == InstallationIdentityCutoverAdvanceStatus.Advanced,
            $"the v2 authority marker did not commit ('{committed.RefusalCode}').");

        var authority = await orchestrator.ResolveAuthorityAsync().ConfigureAwait(false);
        Require(
            authority.Stage == InstallationIdentityCutoverStage.V2Authoritative &&
                authority.Authority == InstallationIdentityAuthorityKind.Revision3V2,
            $"the store did not settle on the v2 authority ('{authority.Stage}' / '{authority.Authority}').");
    }

    private static async Task SeedSourceWatermarkAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        DateTimeOffset now)
    {
        await using var context = await identityFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.MigrationSourceWatermarks.Add(new InstallationIdentityMigrationSourceWatermarkRecord
        {
            SourceKind = "legacy-installation-account",
            SourcePartition = "singleton",
            MigrationRunId = MigrationRunId,
            SourceVersion = 7,
            HighWatermark = "7",
            SnapshotDigest = WatermarkDigest,
            OwnerVersion = 1,
            CapturedAtUtc = now,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static InstallationIdentityCutoverEvidence Evidence() =>
        new(MigrationRunId, WatermarkDigest, VerificationDigest);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "CutoverAdvance could not reach a committed v2 marker: " + message);
        }
    }
}
