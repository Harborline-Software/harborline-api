using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class InstallationIdentityCutoverOrchestratorTests
{
    private const string MigrationRunId = "migration-run-2602";
    private const string WatermarkDigest = "watermark-digest-2602";
    private const string VerificationDigest = "verification-digest-2602";

    [Fact]
    public async Task Lease_IsExclusive_AcrossRestart_AndExpiredHolderIsReclaimedWithNewGeneration()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var firstProcess = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());

        var first = await firstProcess.AcquireLeaseAsync("holder-A", TimeSpan.FromMinutes(1));
        var refused = await firstProcess.AcquireLeaseAsync("holder-B", TimeSpan.FromMinutes(1));

        Assert.Equal(InstallationIdentityLeaseAcquireStatus.Acquired, first.Status);
        Assert.Equal(1, first.Lease!.Generation);
        Assert.Equal(InstallationIdentityLeaseAcquireStatus.HeldByAnother, refused.Status);
        Assert.Null(refused.Lease);

        var alteredCapability = await firstProcess.AdvanceAsync(
            first.Lease! with { ExpiresAtUtc = first.Lease.ExpiresAtUtc.AddMinutes(1) },
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld, alteredCapability.Status);

        // A new service instance models a restarted process reading the same durable lease row.
        var restarted = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var sameHolder = await restarted.AcquireLeaseAsync("holder-A", TimeSpan.FromMinutes(1));
        Assert.Equal(InstallationIdentityLeaseAcquireStatus.AlreadyHeld, sameHolder.Status);
        Assert.Equal(first.Lease, sameHolder.Lease);

        time.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = await restarted.AcquireLeaseAsync("holder-B", TimeSpan.FromMinutes(1));
        Assert.Equal(InstallationIdentityLeaseAcquireStatus.Acquired, reclaimed.Status);
        Assert.Equal(2, reclaimed.Lease!.Generation);
        Assert.NotEqual(first.Lease.LeaseId, reclaimed.Lease.LeaseId);

        var staleAttempt = await restarted.AdvanceAsync(
            first.Lease,
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld, staleAttempt.Status);
        Assert.Equal(InstallationIdentityCutoverStage.LegacyV1Authoritative, staleAttempt.Stage);
    }

    [Fact]
    public async Task Lease_RenewalPreservesIdentity_ReplacesExpiryCapability_AndKeepsHolderLive()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var original = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromMinutes(1))).Lease!;

        time.Advance(TimeSpan.FromSeconds(30));
        var renewed = await service.RenewLeaseAsync(original, TimeSpan.FromMinutes(2));

        Assert.Equal(InstallationIdentityLeaseRenewStatus.Renewed, renewed.Status);
        Assert.NotNull(renewed.Lease);
        Assert.Equal(original.LeaseId, renewed.Lease.LeaseId);
        Assert.Equal(original.Generation, renewed.Lease.Generation);
        Assert.Equal(original.HolderFingerprint, renewed.Lease.HolderFingerprint);
        Assert.True(renewed.Lease.ExpiresAtUtc > original.ExpiresAtUtc);

        var staleExpiry = await service.AdvanceAsync(
            original,
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld, staleExpiry.Status);

        time.Advance(TimeSpan.FromSeconds(45));
        var advanced = await service.AdvanceAsync(
            renewed.Lease,
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.Advanced, advanced.Status);
    }

    [Fact]
    public async Task CleanCompletion_ReleasesLease_AndNextAcquisitionRotatesIdentityAndGeneration()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var original = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;

        await AdvanceThroughVerifiedAsync(service, original);
        var completed = await service.AdvanceAsync(
            original,
            InstallationIdentityCutoverStage.V2Authoritative,
            Evidence());

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.Advanced, completed.Status);
        await using (var context = database.Factory.CreateDbContext())
        {
            var stored = await context.MigrationLeases.AsNoTracking().SingleAsync();
            Assert.Equal(time.GetUtcNow(), stored.ReleasedAtUtc);
        }

        var next = await service.AcquireLeaseAsync("holder-B", TimeSpan.FromHours(1));
        Assert.Equal(InstallationIdentityLeaseAcquireStatus.Acquired, next.Status);
        Assert.Equal(original.Generation + 1, next.Lease!.Generation);
        Assert.NotEqual(original.LeaseId, next.Lease.LeaseId);
    }

    [Fact]
    [Trait("PlanCard", "MTW-01E")]
    public async Task DurableBarrier_Rejects_V1Mutation_And_EveryBearerAudience_AcrossRestart()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());

        var restarted = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var mutation = await restarted.CheckV1MutationAdmissionAsync();
        Assert.False(mutation.IsAllowed);
        Assert.Equal(
            InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal,
            mutation.RefusalCode);
        foreach (var audience in AllLegacyBearerAudiences())
        {
            var bearer = await restarted.CheckLegacyBearerAdmissionAsync(audience);
            Assert.False(bearer.IsAllowed);
            Assert.Equal(
                InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal,
                bearer.RefusalCode);
        }
    }

    [Fact]
    [Trait("PlanCard", "MTW-01E")]
    public async Task MarkerCas_Requires_Exact_Durable_BearerRevocationEvidence()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());
        await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Copying,
            Evidence());
        await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Verified,
            Evidence());

        var premature = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.V2Authoritative,
            Evidence());
        Assert.Equal(
            InstallationIdentityCutoverAdvanceStatus.InvariantRefused,
            premature.Status);
        Assert.Equal("legacy_bearer_revocation_not_staged", premature.RefusalCode);

        var incomplete = await service.StageLegacyBearerRevocationsAsync(
            lease,
            [InstallationIdentityLegacyBearerAudience.Tooling]);
        Assert.Equal(
            InstallationIdentityLegacyBearerRevocationStatus.InvariantRefused,
            incomplete.Status);
        Assert.Equal("legacy_bearer_audience_set_incomplete", incomplete.RefusalCode);

        var staged = await service.StageLegacyBearerRevocationsAsync(
            lease,
            AllLegacyBearerAudiences());
        Assert.Equal(InstallationIdentityLegacyBearerRevocationStatus.Staged, staged.Status);
        var restarted = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var replay = await restarted.StageLegacyBearerRevocationsAsync(
            lease,
            AllLegacyBearerAudiences());
        Assert.Equal(
            InstallationIdentityLegacyBearerRevocationStatus.AlreadyStaged,
            replay.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-01E")]
    public async Task V2Marker_Cas_Has_One_Winner_In_100WayRace_And_Retires_V1_Durably()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var setup = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await setup.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await AdvanceThroughVerifiedAsync(setup, lease);

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ =>
                new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor())
                    .AdvanceAsync(
                        lease,
                        InstallationIdentityCutoverStage.V2Authoritative,
                        Evidence())));

        Assert.Single(
            attempts,
            result => result.Status == InstallationIdentityCutoverAdvanceStatus.Advanced);
        Assert.All(
            attempts.Where(result =>
                result.Status != InstallationIdentityCutoverAdvanceStatus.Advanced),
            result => Assert.Contains(
                result.Status,
                new[]
                {
                    InstallationIdentityCutoverAdvanceStatus.AlreadyAtStage,
                    InstallationIdentityCutoverAdvanceStatus.LeaseNotHeld,
                }));

        var restarted = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var authority = await restarted.ResolveAuthorityAsync();
        Assert.True(authority.IsReadable, authority.RefusalCode);
        Assert.Equal(InstallationIdentityCutoverStage.V2Authoritative, authority.Stage);
        Assert.Equal(InstallationIdentityAuthorityKind.Revision3V2, authority.Authority);
        foreach (var audience in AllLegacyBearerAudiences())
        {
            var bearer = await restarted.CheckLegacyBearerAdmissionAsync(audience);
            Assert.False(bearer.IsAllowed);
            Assert.Equal(
                InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal,
                bearer.RefusalCode);
        }
    }

    [Fact]
    public async Task Advance_RetriesOneDbUpdateConcurrencyFailure_ThenCommitsOnce()
    {
        var interceptor = new ThrowOnceConcurrencyInterceptor();
        await using var database = await TestIdentityDatabase.CreatePreparedAsync(interceptor);
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        interceptor.Arm();

        var advanced = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.WriteBarrierActive,
            Evidence());

        Assert.Equal(1, interceptor.ThrownCount);
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.Advanced, advanced.Status);
        Assert.Equal(InstallationIdentityCutoverStage.WriteBarrierActive, advanced.Stage);
        await using var context = database.Factory.CreateDbContext();
        var state = await context.CutoverStates.AsNoTracking().SingleAsync();
        Assert.Equal(1, state.V1WriteBarrierVersion);
    }

    [Fact]
    public async Task EveryStageBoundary_FaultsBeforeCommit_ThenRestartStillResolvesAnAuthority()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;

        var expectedCurrentStage = InstallationIdentityCutoverStage.LegacyV1Authoritative;
        foreach (var target in new[]
        {
            InstallationIdentityCutoverStage.WriteBarrierActive,
            InstallationIdentityCutoverStage.Copying,
            InstallationIdentityCutoverStage.Verified,
            InstallationIdentityCutoverStage.V2Authoritative,
        })
        {
            if (target == InstallationIdentityCutoverStage.V2Authoritative)
            {
                var staged = await service.StageLegacyBearerRevocationsAsync(
                    lease,
                    AllLegacyBearerAudiences());
                Assert.Contains(
                    staged.Status,
                    new[]
                    {
                        InstallationIdentityLegacyBearerRevocationStatus.Staged,
                        InstallationIdentityLegacyBearerRevocationStatus.AlreadyStaged,
                    });
            }
            await database.InstallKillTriggerAsync(target);
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => service.AdvanceAsync(lease, target, Evidence()));

            // The failed transaction models a process killed between validation and commit. A fresh
            // process must still resolve the authority that owned the preceding stage.
            service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
            var afterFault = await service.ResolveAuthorityAsync();
            Assert.True(afterFault.IsReadable, afterFault.RefusalCode);
            Assert.Equal(expectedCurrentStage, afterFault.Stage);
            Assert.Equal(InstallationIdentityAuthorityKind.LegacyV1, afterFault.Authority);

            await database.RemoveKillTriggerAsync();
            var advanced = await service.AdvanceAsync(lease, target, Evidence());
            Assert.Equal(InstallationIdentityCutoverAdvanceStatus.Advanced, advanced.Status);
            Assert.Equal(target, advanced.Stage);

            service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
            var afterCommit = await service.ResolveAuthorityAsync();
            Assert.True(afterCommit.IsReadable, afterCommit.RefusalCode);
            Assert.Equal(target, afterCommit.Stage);
            Assert.Equal(
                target == InstallationIdentityCutoverStage.V2Authoritative
                    ? InstallationIdentityAuthorityKind.Revision3V2
                    : InstallationIdentityAuthorityKind.LegacyV1,
                afterCommit.Authority);

            expectedCurrentStage = target;
        }
    }

    [Fact]
    public async Task StageCannotSkip_AndVerifiedRequiresDurableCopyEvidence()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;

        var skipped = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Copying,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvalidStageTransition, skipped.Status);
        Assert.Equal("cutover_stage_not_next", skipped.RefusalCode);

        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());

        var missingEvidence = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Verified,
            new InstallationIdentityCutoverEvidence(MigrationRunId));
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, missingEvidence.Status);
        Assert.Equal("cutover_verification_evidence_missing", missingEvidence.RefusalCode);

        var wrongWatermark = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Verified,
            Evidence() with { SourceWatermarkDigest = "not-durable" });
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, wrongWatermark.Status);
        Assert.Equal("source_watermark_unverified", wrongWatermark.RefusalCode);
    }

    [Fact]
    public async Task MigrationRunMismatch_RefusesAdvance_AndLeavesDurableStageUnchanged()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());

        var refused = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Copying,
            Evidence() with { MigrationRunId = "different-migration-run" });

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("migration_run_mismatch", refused.RefusalCode);
        Assert.Equal(InstallationIdentityCutoverStage.WriteBarrierActive, refused.Stage);
        var authority = await service.ResolveAuthorityAsync();
        Assert.True(authority.IsReadable, authority.RefusalCode);
        Assert.Equal(InstallationIdentityCutoverStage.WriteBarrierActive, authority.Stage);
    }

    [Fact]
    public async Task UnresolvedCollision_RefusesVerification_AndLeavesCopyingAuthoritative()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());
        await using (var context = database.Factory.CreateDbContext())
        {
            context.MigrationCollisions.Add(new InstallationIdentityMigrationCollisionRecord
            {
                CollisionId = "collision-2700",
                CollisionKeyDigest = "collision-digest-2700",
                CandidateCount = 2,
                ExpectedSourceVersion = 7,
                Status = InstallationIdentityCollisionStatus.Unresolved,
                OwnerVersion = 1,
                CreatedAtUtc = time.GetUtcNow(),
                UpdatedAtUtc = time.GetUtcNow(),
            });
            await context.SaveChangesAsync();
        }

        var refused = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Verified,
            Evidence());

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("identity_install_scope_collision", refused.RefusalCode);
        Assert.Equal(InstallationIdentityCutoverStage.Copying, refused.Stage);
        var authority = await service.ResolveAuthorityAsync();
        Assert.True(authority.IsReadable, authority.RefusalCode);
        Assert.Equal(InstallationIdentityCutoverStage.Copying, authority.Stage);
    }

    [Fact]
    public async Task RevertingBarrierCheckWouldAllowMutationOrAdvance_AndThisTestFails()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());

        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE installation_identity_cutover_state SET v1_write_barrier_version = 1 " +
                "WHERE singleton_key = 'installation-identity-authority';");
        }

        var admission = await service.CheckV1MutationAdmissionAsync();
        Assert.False(admission.IsAllowed);
        Assert.Equal(InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal, admission.RefusalCode);

        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE installation_identity_cutover_state SET v1_write_barrier_version = 0 " +
                "WHERE singleton_key = 'installation-identity-authority';");
        }

        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());

        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE installation_identity_cutover_state SET v1_write_barrier_version = 0 " +
                "WHERE singleton_key = 'installation-identity-authority';");
        }

        var refused = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.Verified,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("legacy_v1_authority_marker_invalid", refused.RefusalCode);
    }

    [Fact]
    public async Task FinalFlipDiagnosesTheRootCandidateBeforeTheRevocationEvidence()
    {
        // The order-pinning case the sibling test above cannot cover. BOTH preconditions fail here:
        // revocation is never staged, and the root candidate is made unreadable. Whichever check runs
        // first names the refusal, so this is the only shape that distinguishes them.
        //
        // It matters because the two were a single condition until this change, and a reader given
        // "final_verification_not_readable" for a disabled account went looking at the wrong
        // evidence. If a future edit reorders these, the flip is still refused -- but the operator is
        // sent back to the wrong half of the migration, which is how the conflation survived.
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;

        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Verified, Evidence());

        // Deliberately NOT staging legacy bearer revocations.
        await using (var context = database.Factory.CreateDbContext())
        {
            var account = await context.Accounts.SingleAsync();
            account.Status = InstallationAccountStatus.Disabled;
            await context.SaveChangesAsync();
        }

        var refused = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.V2Authoritative,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("successor_not_ready", refused.RefusalCode);
    }

    [Fact]
    public async Task FinalFlipRefusesWhenVerifiedRootStopsBeingReadable()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;

        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Verified, Evidence());
        var staged = await service.StageLegacyBearerRevocationsAsync(
            lease,
            AllLegacyBearerAudiences());
        Assert.Equal(InstallationIdentityLegacyBearerRevocationStatus.Staged, staged.Status);

        await using (var context = database.Factory.CreateDbContext())
        {
            var account = await context.Accounts.SingleAsync();
            account.Status = InstallationAccountStatus.Disabled;
            await context.SaveChangesAsync();
        }

        // This does NOT pin the order of the readiness check against the revocation check, and an
        // earlier revision of this comment claimed it did. Measured: moving the readiness check after
        // the revocation check leaves this test green, because revocation evidence is staged
        // successfully above, so both orderings fall through to the same refusal. Pinning the order
        // would need a case where revocation is NOT staged and the candidate is ALSO unreadable, and
        // no such test exists yet.
        var refused = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.V2Authoritative,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("successor_not_ready", refused.RefusalCode);

        var authority = await service.ResolveAuthorityAsync();
        Assert.True(authority.IsReadable);
        Assert.Equal(InstallationIdentityAuthorityKind.LegacyV1, authority.Authority);
        Assert.Equal(InstallationIdentityCutoverStage.Verified, authority.Stage);
    }

    [Fact]
    public async Task FinalFlipRefusesWhenInstallationIdentityIsNotRevision3Ready()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await AdvanceThroughVerifiedAsync(service, lease);
        await using (var context = database.Factory.CreateDbContext())
        {
            var identity = await context.InstallationIdentities.SingleAsync();
            identity.AuthorityVersion = 0;
            await context.SaveChangesAsync();
        }

        await AssertFinalFlipRefusedAndLegacyRemainsReadableAsync(service, lease);
    }

    [Fact]
    public async Task FinalFlipRefusesWhenInstallationGrantIsNotActive()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time, WithSuccessor());
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await AdvanceThroughVerifiedAsync(service, lease);
        await using (var context = database.Factory.CreateDbContext())
        {
            var grant = await context.InstallationAccessGrants.SingleAsync();
            grant.Status = InstallationAccessGrantStatus.Revoked;
            await context.SaveChangesAsync();
        }

        await AssertFinalFlipRefusedAndLegacyRemainsReadableAsync(service, lease);
    }

    private static async Task AdvanceThroughVerifiedAsync(
        InstallationIdentityCutoverOrchestrator service,
        InstallationIdentityMigrationLease lease)
    {
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Verified, Evidence());
        var staged = await service.StageLegacyBearerRevocationsAsync(
            lease,
            AllLegacyBearerAudiences());
        Assert.Contains(
            staged.Status,
            new[]
            {
                InstallationIdentityLegacyBearerRevocationStatus.Staged,
                InstallationIdentityLegacyBearerRevocationStatus.AlreadyStaged,
            });
    }

    private static async Task AssertFinalFlipRefusedAndLegacyRemainsReadableAsync(
        InstallationIdentityCutoverOrchestrator service,
        InstallationIdentityMigrationLease lease)
    {
        var refused = await service.AdvanceAsync(
            lease,
            InstallationIdentityCutoverStage.V2Authoritative,
            Evidence());
        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("successor_not_ready", refused.RefusalCode);

        var authority = await service.ResolveAuthorityAsync();
        Assert.True(authority.IsReadable, authority.RefusalCode);
        Assert.Equal(InstallationIdentityAuthorityKind.LegacyV1, authority.Authority);
        Assert.Equal(InstallationIdentityCutoverStage.Verified, authority.Stage);
    }

    /// <summary>
    /// A registry whose successor reports ready. Reaching V2Authoritative now REQUIRES this: the
    /// cutover refuses while no v2 sign-in path is registered, which is the point of #3615. Tests
    /// that only exercise the stage machine still have to say so out loud.
    /// </summary>
    private static IInstallationAuthorityVersionRegistry WithSuccessor() =>
        InstallationAuthorityVersionRegistry.CreateDefault(
            [new StubSignInPath("test-successor")]);

    private static InstallationIdentityCutoverEvidence Evidence() =>
        new(MigrationRunId, WatermarkDigest, VerificationDigest);

    private static InstallationIdentityLegacyBearerAudience[] AllLegacyBearerAudiences() =>
        Enum.GetValues<InstallationIdentityLegacyBearerAudience>();

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }

    private sealed class TestIdentityDatabase : IAsyncDisposable
    {
        internal static readonly DateTimeOffset StartedAtUtc =
            new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

        private TestIdentityDatabase(string path, IdentityContextFactory factory)
        {
            Path = path;
            Factory = factory;
        }

        public string Path { get; }

        public IdentityContextFactory Factory { get; }

        public static async Task<TestIdentityDatabase> CreatePreparedAsync(params IInterceptor[] interceptors)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"harborline-cutover-{Guid.NewGuid():N}.db");
            var factory = new IdentityContextFactory(path, interceptors);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }

            var bootstrap = new InstallationFounderBootstrapService(factory, new FixedTimeProvider(StartedAtUtc));
            var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                "$argon2id$v=19$m=19456,t=2,p=1$" +
                    Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "cutover-founder-bootstrap"));
            Assert.Equal(InstallationFounderBootstrapStatus.Created, founder.Status);

            await using (var context = factory.CreateDbContext())
            {
                // The founder bootstrap above now writes the root designation itself
                // (earlier repository ticket #3373), so this harness no longer seeds one. Seeding a second row for
                // the same account violates the UNIQUE constraint on account_id.
                //
                // Nothing is weakened by the removal: HasReadableV2CandidateAsync reads only
                // AccountId and VerifiedAtUtc from the designation, and the ceremony sets both --
                // AccountId to the founder it just created, VerifiedAtUtc to the ceremony instant.
                // The literals this block used to supply (designation-2602 and friends,
                // ExpectedSourceVersion 7) were asserted by no test and read by no production path.
                context.MigrationSourceWatermarks.Add(new InstallationIdentityMigrationSourceWatermarkRecord
                {
                    SourceKind = "legacy-installation-account",
                    SourcePartition = "singleton",
                    MigrationRunId = MigrationRunId,
                    SourceVersion = 7,
                    HighWatermark = "7",
                    SnapshotDigest = WatermarkDigest,
                    OwnerVersion = 1,
                    CapturedAtUtc = StartedAtUtc,
                });
                await context.SaveChangesAsync();
            }

            return new TestIdentityDatabase(path, factory);
        }

        public async Task InstallKillTriggerAsync(InstallationIdentityCutoverStage target)
        {
            var targetStage = target switch
            {
                InstallationIdentityCutoverStage.WriteBarrierActive => "WriteBarrierActive",
                InstallationIdentityCutoverStage.Copying => "Copying",
                InstallationIdentityCutoverStage.Verified => "Verified",
                InstallationIdentityCutoverStage.V2Authoritative => "V2Authoritative",
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
            var triggerSql = """
                CREATE TRIGGER simulate_cutover_process_kill
                BEFORE UPDATE ON installation_identity_cutover_state
                WHEN NEW.stage = '__TARGET_STAGE__'
                BEGIN
                    SELECT RAISE(ABORT, 'simulated process kill');
                END;
                """.Replace("__TARGET_STAGE__", targetStage, StringComparison.Ordinal);
            await using var context = Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync(triggerSql);
        }

        public async Task RemoveKillTriggerAsync()
        {
            await using var context = Factory.CreateDbContext();
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER simulate_cutover_process_kill;");
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowOnceConcurrencyInterceptor : SaveChangesInterceptor
    {
        private int _throwNext;
        private int _thrownCount;

        public int ThrownCount => Volatile.Read(ref _thrownCount);

        public void Arm() => Volatile.Write(ref _throwNext, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _throwNext, 0) == 1)
            {
                Interlocked.Increment(ref _thrownCount);
                throw new DbUpdateConcurrencyException("simulated cutover contention");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    public sealed class IdentityContextFactory(
        string databasePath,
        IReadOnlyList<IInterceptor> interceptors)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>();
            options.UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName));
            options.AddInterceptors(interceptors);
            return new NodeLocalInstallationIdentityDbContext(options.Options);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Versioned authority-policy pipeline (#3615).
    //
    // Housed in this class deliberately: TestIdentityDatabase and MutableTimeProvider are private
    // nested fixtures here, and duplicating ~100 lines of migration and seeding into a second file
    // would make two fixtures that can drift. Sharing the fixture also makes these results directly
    // comparable to the stage-machine tests above.
    //
    // FlipIsRefusedWhenNoSuccessorSignInPathIsRegistered is the one that matters. Every earlier
    // precondition is about DATA, and all of them pass in exactly the state that would leave the
    // installation unable to sign anyone in.
    // ---------------------------------------------------------------------------------------

    private sealed record StubSignInPath(string SignInPathName) : IInstallationAuthorityV2SignInPath;

    private static IInstallationAuthorityVersionRegistry RegistryWith(
        params IInstallationAuthorityV2SignInPath[] signInPaths) =>
        InstallationAuthorityVersionRegistry.CreateDefault(signInPaths);

    private static async Task<InstallationIdentityMigrationLease> DriveToVerifiedAsync(
        InstallationIdentityCutoverOrchestrator service)
    {
        var lease = (await service.AcquireLeaseAsync("holder-A", TimeSpan.FromHours(1))).Lease!;
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.WriteBarrierActive, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Copying, Evidence());
        await service.AdvanceAsync(lease, InstallationIdentityCutoverStage.Verified, Evidence());
        var staged = await service.StageLegacyBearerRevocationsAsync(
            lease,
            Enum.GetValues<InstallationIdentityLegacyBearerAudience>().Order().ToArray());
        Assert.Equal(InstallationIdentityLegacyBearerRevocationStatus.Staged, staged.Status);
        return lease;
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public async Task FlipIsRefusedWhenNoSuccessorSignInPathIsRegistered()
    {
        // Everything the migration itself can produce is in place: the data substrate is ready and
        // the legacy bearer revocations are staged. The ONLY thing missing is somewhere for a human
        // to sign in afterwards, which is the state this installation is in today.
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(
            database.Factory, time, RegistryWith());
        var lease = await DriveToVerifiedAsync(service);

        var refused = await service.AdvanceAsync(
            lease, InstallationIdentityCutoverStage.V2Authoritative, Evidence());

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("successor_not_ready", refused.RefusalCode);

        // And the refusal actually protected something: v1 is still authoritative and still serving.
        var authority = await service.ResolveAuthorityAsync();
        Assert.True(authority.IsReadable);
        Assert.Equal(InstallationIdentityAuthorityKind.LegacyV1, authority.Authority);
        var admission = await service.CheckLegacyBearerAdmissionAsync(
            InstallationIdentityLegacyBearerAudience.AccountChallenge);
        Assert.False(admission.IsAllowed);
        Assert.Equal(
            InstallationIdentityCutoverOrchestrator.MigrationInProgressRefusal,
            admission.RefusalCode);
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public async Task FlipSucceedsOnceASuccessorSignInPathIsRegistered()
    {
        // The same database and the same evidence as the refusal above. The ONLY difference is a
        // registered successor, which is what makes this pair a control rather than two assertions.
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(
            database.Factory, time, RegistryWith(new StubSignInPath("v2-challenge-issuer")));
        var lease = await DriveToVerifiedAsync(service);

        var advanced = await service.AdvanceAsync(
            lease, InstallationIdentityCutoverStage.V2Authoritative, Evidence());

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.Advanced, advanced.Status);
        Assert.Equal(InstallationIdentityCutoverStage.V2Authoritative, advanced.Stage);
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public async Task RetirementRefusalAfterTheFlipComesFromTheSuccessorPolicy()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(
            database.Factory, time, RegistryWith(new StubSignInPath("v2-challenge-issuer")));
        var lease = await DriveToVerifiedAsync(service);
        var advanced = await service.AdvanceAsync(
            lease, InstallationIdentityCutoverStage.V2Authoritative, Evidence());

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.Advanced, advanced.Status);
        await using (var context = database.Factory.CreateDbContext())
        {
            var durableState = await context.CutoverStates.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCutoverStage.V2Authoritative, durableState.Stage);
            Assert.Equal(
                InstallationIdentityCutoverStateRecord.Revision3V2AuthorityVersion,
                durableState.AuthorityVersion);
        }

        // Every audience is refused, and with the code the SUCCESSOR names rather than one the stage
        // machine hardcodes. A v3 retiring a different set changes this answer by adding a policy.
        foreach (var audience in Enum.GetValues<InstallationIdentityLegacyBearerAudience>())
        {
            var admission = await service.CheckLegacyBearerAdmissionAsync(audience);
            Assert.False(admission.IsAllowed);
            Assert.Equal(
                InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal,
                admission.RefusalCode);
        }

        // The v1 mutation path reports retirement too. It previously reported "migration in
        // progress" after the flip, because the retirement ternary existed at only one of the two
        // call sites -- the migration had finished and the authority was gone.
        var mutation = await service.CheckV1MutationAdmissionAsync();
        Assert.False(mutation.IsAllowed);
        Assert.Equal(
            InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal,
            mutation.RefusalCode);
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public void RegistryRefusesAnUnknownVersionRatherThanDefaultingPermissively()
    {
        var registry = new InstallationAuthorityVersionRegistry(
            [new V1InstallationAuthorityVersionPolicy()]);

        Assert.Equal(
            InstallationAuthorityVersion.V1,
            registry.Get(InstallationAuthorityVersion.V1).Version);
        // A missing policy must throw. Answering "allowed" for a version nobody wrote rules for is
        // how a version check silently stops being a check.
        Assert.Throws<InvalidOperationException>(
            () => registry.Get(InstallationAuthorityVersion.V2));
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public async Task UnknownDurableStageThrowsInsteadOfResolvingToThePermissiveV1Policy()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        await using (var context = database.Factory.CreateDbContext())
        {
            var state = await context.CutoverStates.SingleAsync();
            state.Stage = (InstallationIdentityCutoverStage)int.MaxValue;
            await context.SaveChangesAsync();
        }

        var service = new InstallationIdentityCutoverOrchestrator(
            database.Factory, new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CheckLegacyBearerAdmissionAsync(
                InstallationIdentityLegacyBearerAudience.AccountChallenge));

        Assert.Equal("stage", exception.ParamName);
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public void RegistryRefusesTwoPoliciesForOneVersion()
    {
        // Otherwise the effective rule set depends on DI registration order.
        Assert.Throws<InvalidOperationException>(() => new InstallationAuthorityVersionRegistry(
            [new V1InstallationAuthorityVersionPolicy(), new V1InstallationAuthorityVersionPolicy()]));
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public async Task DefaultCompositionIsFailClosed()
    {
        // The two-argument orchestrator constructor exists so 30-odd call sites keep compiling. It
        // must not be a back door: its default registry gives V2 no sign-in paths, so a caller that
        // forgets to supply one gets a refused cutover rather than a permitted one.
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        var time = new MutableTimeProvider(TestIdentityDatabase.StartedAtUtc);
        var service = new InstallationIdentityCutoverOrchestrator(database.Factory, time);
        var lease = await DriveToVerifiedAsync(service);

        var refused = await service.AdvanceAsync(
            lease, InstallationIdentityCutoverStage.V2Authoritative, Evidence());

        Assert.Equal(InstallationIdentityCutoverAdvanceStatus.InvariantRefused, refused.Status);
        Assert.Equal("successor_not_ready", refused.RefusalCode);
    }

    [Fact]
    [Trait("PlanCard", "MTW-3615")]
    public async Task V1PolicyAdmitsOnlyBeforeItsBarrier_V2PolicyNeverAdmitsLegacyRequests()
    {
        await using var database = await TestIdentityDatabase.CreatePreparedAsync();
        await using var context = database.Factory.CreateDbContext();
        var state = await context.CutoverStates.SingleAsync();

        var v1 = new V1InstallationAuthorityVersionPolicy();
        Assert.True(await v1.IsReadyAsync(context, CancellationToken.None));
        Assert.Null(v1.RetirementRefusalCode);
        Assert.Empty(v1.RetiredAudiences);
        Assert.True(await v1.IsAdmissionAllowedAsync(
            state.Stage,
            state.V1WriteBarrierVersion,
            InstallationCutoverAdmissionKind.LegacyBearer,
            InstallationIdentityLegacyBearerAudience.AccountChallenge,
            CancellationToken.None));
        state.V1WriteBarrierVersion = 1;
        Assert.False(await v1.IsAdmissionAllowedAsync(
            state.Stage,
            state.V1WriteBarrierVersion,
            InstallationCutoverAdmissionKind.LegacyBearer,
            InstallationIdentityLegacyBearerAudience.AccountChallenge,
            CancellationToken.None));

        var v2WithNoPath = new V2InstallationAuthorityVersionPolicy([]);
        Assert.False(await v2WithNoPath.IsReadyAsync(context, CancellationToken.None));
        Assert.False(await v2WithNoPath.IsAdmissionAllowedAsync(
            state.Stage,
            state.V1WriteBarrierVersion,
            InstallationCutoverAdmissionKind.LegacyBearer,
            InstallationIdentityLegacyBearerAudience.AccountChallenge,
            CancellationToken.None));

        // BOTH halves are required, proved in both directions on the same policy instance. The
        // prepared fixture already carries a readable v2 candidate, so with a registered path the
        // policy reports ready...
        var v2WithPath = new V2InstallationAuthorityVersionPolicy(
            [new StubSignInPath("v2-challenge-issuer")]);
        Assert.True(await v2WithPath.IsReadyAsync(context, CancellationToken.None));

        // ...and removing the DATA half alone takes it back to not-ready, so the sign-in-path check
        // cannot be mistaken for the only thing this predicate looks at.
        var account = await context.Accounts.SingleAsync();
        account.Status = InstallationAccountStatus.Disabled;
        await context.SaveChangesAsync();
        Assert.False(await v2WithPath.IsReadyAsync(context, CancellationToken.None));
    }
}
