using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

[Trait("PlanCard", "MIG-01B+MIG-02A+MIG-02B")]
public sealed class LegacyIdentityMigrationServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 18, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Inventory_IsByteEquivalentAcrossRandomSourceOrder_AndOptionsCollisionIsClassified()
    {
        var store = new FakeLegacyStore(
            Snapshot("tenant-b", 9, "9", Row("bob", "bob@example.test", 2, credentialed: true)),
            Snapshot("tenant-a", 4, "4", Row("alice", " Alice@example.test ", 3, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions
        {
            FounderUsername = "ＡＬＩＣＥ@example.test",
            FounderPasswordHash = "configured-secret-artifact",
        });

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();

        Assert.Equal(first.SnapshotDigest, second.SnapshotDigest);
        Assert.Equal(first.Rows, second.Rows);
        Assert.Equal(first.Watermarks, second.Watermarks);
        Assert.Equal(
            first.Rows.OrderBy(row => row.CompositeKey.CanonicalValue, StringComparer.Ordinal),
            first.Rows);
        Assert.All(first.Rows, row => Assert.Equal(64, row.DeterministicInstallationAccountId.Length));
        var founder = Assert.Single(
            first.Rows,
            row => row.Mutability == LegacyWebIdentitySourceMutability.ImmutableOptionsFounder);
        Assert.Equal(LegacyWebIdentityInventoryReader.ImmutableOptionsOwnerVersion, founder.OwnerVersion);
        Assert.Equal("immutable:0", founder.SourceHighWatermark);

        var collision = Assert.Single(first.Collisions);
        Assert.Equal(2, collision.CandidateCount);
        Assert.Equal(
            LegacyUsernameCollisionRepairability.ImmutableSourceRefusal,
            collision.Repairability);
        Assert.DoesNotContain("ALICE", collision.CollisionId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.RenameCalls);
    }

    [Fact]
    public async Task Inventory_RejectsDuplicatePartitions_AndCanonicalFramingIsUnambiguous()
    {
        var store = new FakeLegacyStore(
            Snapshot("tenant-a", 1, "one", Row("account-a", "a@example.test", 1, credentialed: true)),
            Snapshot("tenant-a", 2, "two", Row("account-b", "b@example.test", 1, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync());

        Assert.Equal("identity.legacy_source_partition_duplicate", exception.Message);
        Assert.NotEqual(
            LegacyIdentityCanonicalFraming.Frame("kind", "tenant|1", "2", "w", "digest"),
            LegacyIdentityCanonicalFraming.Frame("kind", "tenant", "1", "2|w", "digest"));
    }

    [Fact]
    public async Task SqliteLegacyStore_ExercisesDbContract_AndSerializesConcurrentTargetClaims()
    {
        await using var store = await SqliteLegacyStore.CreateAsync(Snapshot(
            "tenant-a",
            7,
            "7",
            Row("account-a", "alpha@example.test", 2, credentialed: true),
            Row("account-b", "bravo@example.test", 3, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());
        var before = await reader.ReadAsync();
        var keys = before.Rows.Select(row => row.CompositeKey).ToArray();

        var attempts = await Task.WhenAll(
            store.RenameAsync(keys[0], "CLAIMED@EXAMPLE.TEST", 2, "claim-a", CancellationToken.None),
            store.RenameAsync(keys[1], "CLAIMED@EXAMPLE.TEST", 3, "claim-b", CancellationToken.None));
        var after = await reader.ReadAsync();

        Assert.Single(attempts, result => result.Status == LegacyWebIdentitySourceRenameStatus.Renamed);
        Assert.Single(
            attempts,
            result => result.Status == LegacyWebIdentitySourceRenameStatus.TargetUsernameCollision);
        Assert.Single(after.Rows, row => row.NormalizedUsername == "CLAIMED@EXAMPLE.TEST");
        Assert.Equal(7, Assert.Single(after.Watermarks).SourceVersion);
        Assert.Equal(
            before.Rows.Sum(row => row.OwnerVersion) + 1,
            after.Rows.Sum(row => row.OwnerVersion));
    }

    [Fact]
    public async Task TombstoneProjection_IsIdempotent_PreservesAuditProvenance_AndFabricatesNoAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            12,
            "12",
            Row(
                "pending-invite",
                "pending@example.test",
                5,
                credentialed: false,
                uncredentialedInvite: true,
                auditCorrelationId: "tenant-a-invite-audit-12"),
            Row("credentialed", "person@example.test", 6, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());
        var projector = new LegacyInviteTombstoneProjector(database.Factory, database.TimeProvider);
        var inventory = await reader.ReadAsync();

        var first = await projector.ProjectAsync(inventory);
        var replay = await projector.ProjectAsync(inventory);

        Assert.Equal(new LegacyInviteTombstoneProjectionResult(1, 0), first);
        Assert.Equal(new LegacyInviteTombstoneProjectionResult(0, 1), replay);
        await using var context = database.Factory.CreateDbContext();
        var tombstone = await context.MigrationTombstones.AsNoTracking().SingleAsync();
        Assert.Equal("tenant-a-invite-audit-12", tombstone.AuditCorrelationId);
        Assert.Equal(LegacyInviteTombstoneProjector.UncredentialedInviteReason, tombstone.ReasonCode);
        Assert.Equal(12, tombstone.SourceVersion);
        Assert.Empty(await context.Accounts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.InstallationAccessGrants.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.MigrationCollisions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.MigrationSourceWatermarks.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Rename_ChangesOnlyDbUsername_AppendsInstallationAudit_AndReplaysIdempotently()
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            21,
            "21",
            Row("account-a", "duplicate@example.test", 7, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 11, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions
        {
            FounderUsername = "root@example.test",
            FounderPasswordHash = "configured-secret-artifact",
        });
        var authority = new FakeRecoveryAuthority(database.RecoveryEvidence);
        var service = new LegacyWebAccountRenameService(
            reader,
            store,
            database.Factory,
            authority,
            database.TimeProvider);
        var key = new LegacyWebAccountCompositeKey(
            LegacyWebIdentityInventoryReader.DatabaseSourceKind,
            "tenant-a",
            "account-a");
        var command = new RenameLegacyWebAccountCommand(
            key,
            "renamed@example.test",
            "repair-account-a",
            7);
        var protectedFacts = store.ProtectedFacts;

        var result = await service.ExecuteAsync(command);
        var replay = await service.ExecuteAsync(command);
        var changedReplay = await service.ExecuteAsync(command with
        {
            NewNormalizedUsername = "different@example.test",
        });

        Assert.Equal(RenameLegacyWebAccountStatus.Renamed, result.Status);
        Assert.Equal(RenameLegacyWebAccountStatus.IdempotentReplay, replay.Status);
        Assert.Equal(RenameLegacyWebAccountStatus.ChangedReplay, changedReplay.Status);
        Assert.Equal(1, store.RenameCalls);
        Assert.Equal(protectedFacts, store.ProtectedFacts);
        Assert.Equal("RENAMED@EXAMPLE.TEST", store.RequiredRow("tenant-a", "account-a").Username);
        Assert.Equal("DUPLICATE@example.test", store.RequiredRow("tenant-a", "account-b").Username);

        await using var context = database.Factory.CreateDbContext();
        var collision = await context.MigrationCollisions.AsNoTracking().SingleAsync();
        Assert.Equal(InstallationIdentityCollisionStatus.Renamed, collision.Status);
        Assert.Equal(2, collision.CandidateCount);
        Assert.Equal(2, await context.AuditEnvelopes.CountAsync());
        var renameAudit = await context.AuditEnvelopes.AsNoTracking()
            .SingleAsync(row => row.EventType == "LegacyWebAccountRenamed");
        Assert.Equal("os-bound-installation-recovery-principal", renameAudit.ActorKind);
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(renameAudit));
        var head = await context.AuditHeads.AsNoTracking().SingleAsync();
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .OrderBy(row => row.Sequence)
            .ToArrayAsync();
        Assert.True(InstallationAuditIntegrity.HasValidChain(
            chain,
            head,
            renameAudit.InstallationIdentityId));
        Assert.Single(await context.Accounts.AsNoTracking().ToArrayAsync());
        Assert.Single(await context.InstallationAccessGrants.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, authority.RequireCalls);
    }

    [Fact]
    public async Task Rename_RetryFinalizesAuditAfterCrashBetweenSourceRenameAndAuditCommit()
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            21,
            "21",
            Row("account-a", "duplicate@example.test", 7, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 11, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());
        var authority = new FakeRecoveryAuthority(database.RecoveryEvidence);
        var failBeforeAudit = new FailOnceIdentityContextFactory(database.Factory, failOnCall: 5);
        var service = new LegacyWebAccountRenameService(
            reader,
            store,
            failBeforeAudit,
            authority,
            database.TimeProvider);
        var command = new RenameLegacyWebAccountCommand(
            new LegacyWebAccountCompositeKey(
                LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                "tenant-a",
                "account-a"),
            "renamed@example.test",
            "repair-after-interruption",
            7);

        await Assert.ThrowsAsync<SimulatedInterruptionException>(() => service.ExecuteAsync(command));
        var retryingAuthority = new FakeRecoveryAuthority(database.RecoveryEvidence with
        {
            ActorId = "different-retrying-principal",
        });
        var resumed = await new LegacyWebAccountRenameService(
            reader,
            store,
            database.Factory,
            retryingAuthority,
            database.TimeProvider).ExecuteAsync(command);

        Assert.Equal(RenameLegacyWebAccountStatus.Renamed, resumed.Status);
        Assert.Equal(1, store.RenameCalls);
        Assert.Equal("RENAMED@EXAMPLE.TEST", store.RequiredRow("tenant-a", "account-a").Username);
        await using var context = database.Factory.CreateDbContext();
        var collision = await context.MigrationCollisions.AsNoTracking().SingleAsync();
        Assert.Equal(InstallationIdentityCollisionStatus.Renamed, collision.Status);
        var renameAudit = await context.AuditEnvelopes.AsNoTracking()
            .SingleAsync(row => row.EventType == "LegacyWebAccountRenamed");
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(renameAudit));
        Assert.Equal(database.RecoveryEvidence.ActorId, renameAudit.ActorId);
        Assert.Equal(1, authority.RequireCalls);
        Assert.Equal(0, retryingAuthority.RequireCalls);
        Assert.Equal(database.RecoveryEvidence.ActorId, collision.RecoveryActorId);
        Assert.Equal(21, collision.ExpectedSourceVersion);
        Assert.Equal(7, collision.ExpectedOwnerVersion);
    }

    [Fact]
    public async Task Rename_ChangedCommandCannotTakeOverInterruptedCheckpoint()
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            21,
            "21",
            Row("account-a", "duplicate@example.test", 7, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 11, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());
        var authority = new FakeRecoveryAuthority(database.RecoveryEvidence);
        var service = new LegacyWebAccountRenameService(
            reader,
            store,
            new FailOnceIdentityContextFactory(database.Factory, failOnCall: 5),
            authority,
            database.TimeProvider);
        var command = new RenameLegacyWebAccountCommand(
            new LegacyWebAccountCompositeKey(
                LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                "tenant-a",
                "account-a"),
            "renamed@example.test",
            "checkpoint-binding",
            7);

        await Assert.ThrowsAsync<SimulatedInterruptionException>(() => service.ExecuteAsync(command));
        var changed = await service.ExecuteAsync(command with
        {
            NewNormalizedUsername = "substituted-target@example.test",
        });

        Assert.Equal(RenameLegacyWebAccountStatus.ChangedReplay, changed.Status);
        Assert.Equal(1, store.RenameCalls);
        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == "LegacyWebAccountRenamed")
            .ToArrayAsync());
    }

    [Theory]
    [InlineData(CheckpointAlteration.SourceKey)]
    [InlineData(CheckpointAlteration.IdempotencyKey)]
    [InlineData(CheckpointAlteration.ExpectedOwnerVersion)]
    [InlineData(CheckpointAlteration.SourceSnapshotVersion)]
    [InlineData(CheckpointAlteration.RecoveryRootEvidence)]
    public async Task Rename_InterruptedCheckpointRejectsEachAlteredBinding(
        CheckpointAlteration alteration)
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            21,
            "21",
            Row("account-a", "duplicate@example.test", 7, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 11, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());
        var authority = new FakeRecoveryAuthority(database.RecoveryEvidence);
        var service = new LegacyWebAccountRenameService(
            reader,
            store,
            new FailOnceIdentityContextFactory(database.Factory, failOnCall: 5),
            authority,
            database.TimeProvider);
        var command = new RenameLegacyWebAccountCommand(
            new LegacyWebAccountCompositeKey(
                LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                "tenant-a",
                "account-a"),
            "renamed@example.test",
            "checkpoint-one-binding",
            7);

        await Assert.ThrowsAsync<SimulatedInterruptionException>(() => service.ExecuteAsync(command));
        var alteredCommand = alteration switch
        {
            CheckpointAlteration.SourceKey => command with
            {
                ExpectedCompositeKey = command.ExpectedCompositeKey with
                {
                    LegacyAccountId = "account-b",
                },
            },
            CheckpointAlteration.IdempotencyKey => command with
            {
                IdempotencyKey = "checkpoint-different-idempotency",
            },
            CheckpointAlteration.ExpectedOwnerVersion => command with
            {
                ExpectedOwnerVersion = 8,
            },
            _ => command,
        };
        if (alteration == CheckpointAlteration.SourceSnapshotVersion)
        {
            store.SetSourceVersion("tenant-a", 22);
        }
        if (alteration == CheckpointAlteration.RecoveryRootEvidence)
        {
            await using var tamperContext = database.Factory.CreateDbContext();
            var checkpoint = await tamperContext.MigrationCollisions.SingleAsync();
            checkpoint.RecoveryRootEpoch++;
            await tamperContext.SaveChangesAsync();
        }

        var retryService = new LegacyWebAccountRenameService(
            reader,
            store,
            database.Factory,
            authority,
            database.TimeProvider);
        if (alteration == CheckpointAlteration.RecoveryRootEvidence)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                retryService.ExecuteAsync(alteredCommand));
            Assert.Equal("identity.recovery_principal_evidence_stale", exception.Message);
        }
        else
        {
            var rejected = await retryService.ExecuteAsync(alteredCommand);
            Assert.Contains(
                rejected.Status,
                new[]
                {
                    RenameLegacyWebAccountStatus.ChangedReplay,
                    RenameLegacyWebAccountStatus.SourceVersionStale,
                });
        }

        Assert.Equal(1, store.RenameCalls);
        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == "LegacyWebAccountRenamed")
            .ToArrayAsync());
    }

    [Fact]
    public async Task Rename_DetachedCompletedEnvelopeCannotAuthorizeCheckpointTakeover()
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            21,
            "21",
            Row("account-a", "duplicate@example.test", 7, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 11, credentialed: true)));
        var collisionId = LegacyWebIdentityInventoryReader.CollisionId("DUPLICATE@EXAMPLE.TEST");
        await using (var context = database.Factory.CreateDbContext())
        {
            var identity = await context.InstallationIdentities.SingleAsync();
            var head = await context.AuditHeads.SingleAsync();
            var root = await context.RootKeyEpochs.SingleAsync(row =>
                row.Status == InstallationRootEpochStatus.Active);
            var detached = new InstallationAuditEnvelopeRecord
            {
                InstallationIdentityId = identity.InstallationIdentityId,
                Sequence = head.Sequence + 1,
                CorrelationId = "detached-completed-repair",
                CommandFingerprint = "detached-command",
                EventType = "LegacyWebAccountRenamed",
                ActorKind = "os-bound-installation-recovery-principal",
                ActorId = database.RecoveryEvidence.ActorId,
                RootEpoch = root.EpochNumber,
                RootPublicKeyFingerprint = root.RootPublicKeyFingerprint,
                PreviousHash = head.HeadHash,
                EnvelopeHash = string.Empty,
                PayloadDigest = InstallationAuditIntegrity.Hash("detached-payload"),
                OccurredAtUtc = FixedNow,
            };
            detached.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(detached);
            context.AuditEnvelopes.Add(detached);
            context.MigrationCollisions.Add(new InstallationIdentityMigrationCollisionRecord
            {
                CollisionId = collisionId,
                CollisionKeyDigest = collisionId,
                CandidateCount = 2,
                ExpectedSourceVersion = 21,
                Status = InstallationIdentityCollisionStatus.Unresolved,
                RepairIdempotencyKeyDigest = "prior-repair",
                AuditCorrelationId = detached.CorrelationId,
                OwnerVersion = 1,
                CreatedAtUtc = FixedNow,
                UpdatedAtUtc = FixedNow,
            });
            await context.SaveChangesAsync();
        }
        var service = new LegacyWebAccountRenameService(
            new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions()),
            store,
            database.Factory,
            new FakeRecoveryAuthority(database.RecoveryEvidence),
            database.TimeProvider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            new RenameLegacyWebAccountCommand(
                new LegacyWebAccountCompositeKey(
                    LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                    "tenant-a",
                    "account-a"),
                "renamed@example.test",
                "replacement-repair",
                7)));

        Assert.Equal("identity.legacy_rename_repair_in_progress", exception.Message);
        Assert.Equal(0, store.RenameCalls);
    }

    [Fact]
    public async Task Rename_CompletedRepairMustMatchCheckpointPrincipalBeforeTakeover()
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            21,
            "21",
            Row("account-a", "duplicate@example.test", 7, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 11, credentialed: true),
            Row("account-c", "duplicate@example.test", 13, credentialed: true)));
        var service = new LegacyWebAccountRenameService(
            new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions()),
            store,
            database.Factory,
            new FakeRecoveryAuthority(database.RecoveryEvidence),
            database.TimeProvider);

        var first = await service.ExecuteAsync(new RenameLegacyWebAccountCommand(
            new LegacyWebAccountCompositeKey(
                LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                "tenant-a",
                "account-a"),
            "first-repair@example.test",
            "first-completed-repair",
            7));
        Assert.Equal(RenameLegacyWebAccountStatus.RenamedCollisionRemaining, first.Status);
        await using (var tamperContext = database.Factory.CreateDbContext())
        {
            var checkpoint = await tamperContext.MigrationCollisions.SingleAsync();
            checkpoint.RecoveryActorId = "substituted-checkpoint-principal";
            await tamperContext.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            new RenameLegacyWebAccountCommand(
                new LegacyWebAccountCompositeKey(
                    LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                    "tenant-a",
                    "account-b"),
                "second-repair@example.test",
                "second-repair-attempt",
                11)));

        Assert.Equal("identity.legacy_rename_repair_in_progress", exception.Message);
        Assert.Equal(1, store.RenameCalls);
    }

    [Fact]
    public async Task FounderCollision_IsAClassifiedRefusal_WithNoRenameOrAuditWrite()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            3,
            "3",
            Row("account-a", "founder@example.test", 2, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions
        {
            FounderUsername = "FOUNDER@example.test",
            FounderPasswordHash = "configured-secret-artifact",
        });
        var authority = new FakeRecoveryAuthority(new InstallationRecoveryPrincipalEvidence(
            "unused",
            1,
            string.Join(":", Enumerable.Repeat("AA", 32))));
        var service = new LegacyWebAccountRenameService(
            reader,
            store,
            database.Factory,
            authority,
            database.TimeProvider);

        var result = await service.ExecuteAsync(new RenameLegacyWebAccountCommand(
            new LegacyWebAccountCompositeKey(
                LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                "tenant-a",
                "account-a"),
            "renamed@example.test",
            "immutable-collision-repair",
            2));

        Assert.Equal(RenameLegacyWebAccountStatus.ImmutableSourceRefused, result.Status);
        Assert.Equal(2, result.ClassifiedCandidateCount);
        Assert.Equal(0, store.RenameCalls);
        Assert.Equal(0, authority.RequireCalls);
        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.MigrationCollisions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task StaleOwnerVersionAndTargetCollision_RefuseBeforeAnyMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            8,
            "8",
            Row("account-a", "duplicate@example.test", 4, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 5, credentialed: true),
            Row("account-c", "occupied@example.test", 6, credentialed: true)));
        var reader = new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions());
        var authority = new FakeRecoveryAuthority(new InstallationRecoveryPrincipalEvidence(
            "unused",
            1,
            string.Join(":", Enumerable.Repeat("AA", 32))));
        var service = new LegacyWebAccountRenameService(
            reader,
            store,
            database.Factory,
            authority,
            database.TimeProvider);
        var key = new LegacyWebAccountCompositeKey(
            LegacyWebIdentityInventoryReader.DatabaseSourceKind,
            "tenant-a",
            "account-a");

        var stale = await service.ExecuteAsync(new RenameLegacyWebAccountCommand(
            key,
            "free@example.test",
            "stale",
            99));
        var occupied = await service.ExecuteAsync(new RenameLegacyWebAccountCommand(
            key,
            "occupied@example.test",
            "occupied",
            4));

        Assert.Equal(RenameLegacyWebAccountStatus.SourceVersionStale, stale.Status);
        Assert.Equal(RenameLegacyWebAccountStatus.TargetUsernameCollision, occupied.Status);
        Assert.Equal(0, store.RenameCalls);
        Assert.Equal(0, authority.RequireCalls);
        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.MigrationCollisions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Rename_AtomicTargetRefusalClearsCheckpoint_ForDifferentTargetRetry()
    {
        await using var database = await TestDatabase.CreateWithFounderAsync();
        var store = new FakeLegacyStore(Snapshot(
            "tenant-a",
            8,
            "8",
            Row("account-a", "duplicate@example.test", 4, credentialed: true),
            Row("account-b", "DUPLICATE@example.test", 5, credentialed: true)));
        store.ForceNextRenameStatus(LegacyWebIdentitySourceRenameStatus.TargetUsernameCollision);
        var service = new LegacyWebAccountRenameService(
            new LegacyWebIdentityInventoryReader(store, new NodeWebClientOptions()),
            store,
            database.Factory,
            new FakeRecoveryAuthority(database.RecoveryEvidence),
            database.TimeProvider);
        var key = new LegacyWebAccountCompositeKey(
            LegacyWebIdentityInventoryReader.DatabaseSourceKind,
            "tenant-a",
            "account-a");

        var refused = await service.ExecuteAsync(new RenameLegacyWebAccountCommand(
            key,
            "claimed@example.test",
            "atomic-race-refusal",
            4));
        var retried = await service.ExecuteAsync(new RenameLegacyWebAccountCommand(
            key,
            "alternate@example.test",
            "atomic-race-retry",
            4));

        Assert.Equal(RenameLegacyWebAccountStatus.TargetUsernameCollision, refused.Status);
        Assert.Equal(RenameLegacyWebAccountStatus.Renamed, retried.Status);
        Assert.Equal("ALTERNATE@EXAMPLE.TEST", store.RequiredRow("tenant-a", "account-a").Username);
        Assert.Equal(1, store.RenameCalls);
        await using var context = database.Factory.CreateDbContext();
        Assert.Single(await context.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == "LegacyWebAccountRenamed")
            .ToArrayAsync());
    }

    private static DbBackedLegacyWebIdentitySnapshot Snapshot(
        string partition,
        long version,
        string highWatermark,
        params DbBackedLegacyWebIdentityRow[] rows) =>
        new(partition, version, highWatermark, rows);

    private static DbBackedLegacyWebIdentityRow Row(
        string id,
        string username,
        long ownerVersion,
        bool credentialed,
        bool uncredentialedInvite = false,
        string? auditCorrelationId = null) =>
        new(
            id,
            username,
            ownerVersion,
            credentialed,
            uncredentialedInvite,
            auditCorrelationId ?? $"audit-{id}");

    public enum CheckpointAlteration
    {
        SourceKey,
        IdempotencyKey,
        ExpectedOwnerVersion,
        SourceSnapshotVersion,
        RecoveryRootEvidence,
    }

    private sealed class FakeLegacyStore : IDbBackedLegacyWebIdentityStore
    {
        private readonly List<MutableSnapshot> _snapshots;
        private readonly Dictionary<string, string> _replays = new(StringComparer.Ordinal);
        private readonly Queue<LegacyWebIdentitySourceRenameStatus> _forcedRenameStatuses = new();
        private bool _reverse;

        internal FakeLegacyStore(params DbBackedLegacyWebIdentitySnapshot[] snapshots)
        {
            _snapshots = snapshots.Select(snapshot => new MutableSnapshot(
                snapshot.SourcePartition,
                snapshot.SourceVersion,
                snapshot.HighWatermark,
                snapshot.Rows.ToList())).ToList();
            ProtectedFacts = InstallationAuditIntegrity.Hash(
                _snapshots.SelectMany(snapshot => snapshot.Rows)
                    .OrderBy(row => row.LegacyAccountId, StringComparer.Ordinal)
                    .Select(row => $"{row.LegacyAccountId}|credential:{row.HasCredential}|invite:{row.IsUncredentialedInvite}")
                    .ToArray());
        }

        internal int RenameCalls { get; private set; }

        internal string ProtectedFacts { get; }

        internal void ForceNextRenameStatus(LegacyWebIdentitySourceRenameStatus status) =>
            _forcedRenameStatuses.Enqueue(status);

        internal void SetSourceVersion(string partition, long sourceVersion)
        {
            var snapshot = _snapshots.Single(item => item.SourcePartition == partition);
            snapshot.SourceVersion = sourceVersion;
        }

        public Task<IReadOnlyList<DbBackedLegacyWebIdentitySnapshot>> ReadSnapshotsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reverse = !_reverse;
            var snapshots = _snapshots.Select(snapshot => new DbBackedLegacyWebIdentitySnapshot(
                    snapshot.SourcePartition,
                    snapshot.SourceVersion,
                    snapshot.HighWatermark,
                    (_reverse ? snapshot.Rows.AsEnumerable().Reverse() : snapshot.Rows).ToArray()))
                .ToArray();
            if (_reverse)
            {
                Array.Reverse(snapshots);
            }
            return Task.FromResult<IReadOnlyList<DbBackedLegacyWebIdentitySnapshot>>(snapshots);
        }

        public Task<LegacyWebIdentitySourceRenameResult> RenameAsync(
            LegacyWebAccountCompositeKey key,
            string normalizedUsername,
            long expectedOwnerVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = InstallationAuditIntegrity.Hash(
                key.CanonicalValue,
                normalizedUsername,
                expectedOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (_replays.TryGetValue(idempotencyKey, out var replayFingerprint))
            {
                return Task.FromResult(new LegacyWebIdentitySourceRenameResult(
                    string.Equals(replayFingerprint, fingerprint, StringComparison.Ordinal)
                        ? LegacyWebIdentitySourceRenameStatus.IdempotentReplay
                        : LegacyWebIdentitySourceRenameStatus.ChangedReplay));
            }
            if (_forcedRenameStatuses.TryDequeue(out var forcedStatus))
            {
                return Task.FromResult(new LegacyWebIdentitySourceRenameResult(forcedStatus));
            }

            var snapshot = _snapshots.SingleOrDefault(item =>
                string.Equals(item.SourcePartition, key.SourcePartition, StringComparison.Ordinal));
            var index = snapshot?.Rows.FindIndex(row =>
                string.Equals(row.LegacyAccountId, key.LegacyAccountId, StringComparison.Ordinal)) ?? -1;
            if (snapshot is null || index < 0)
            {
                return Task.FromResult(new LegacyWebIdentitySourceRenameResult(
                    LegacyWebIdentitySourceRenameStatus.SourceNotFound));
            }
            var row = snapshot.Rows[index];
            if (row.OwnerVersion != expectedOwnerVersion)
            {
                return Task.FromResult(new LegacyWebIdentitySourceRenameResult(
                    LegacyWebIdentitySourceRenameStatus.SourceVersionStale));
            }
            if (_snapshots.Any(item => item.Rows.Any(candidate =>
                !(string.Equals(item.SourcePartition, key.SourcePartition, StringComparison.Ordinal)
                    && string.Equals(candidate.LegacyAccountId, key.LegacyAccountId, StringComparison.Ordinal))
                && string.Equals(
                    LegacyWebIdentityInventoryReader.NormalizeUsername(candidate.Username),
                    normalizedUsername,
                    StringComparison.Ordinal))))
            {
                return Task.FromResult(new LegacyWebIdentitySourceRenameResult(
                    LegacyWebIdentitySourceRenameStatus.TargetUsernameCollision));
            }

            snapshot.Rows[index] = row with
            {
                Username = normalizedUsername,
                OwnerVersion = row.OwnerVersion + 1,
            };
            _replays.Add(idempotencyKey, fingerprint);
            RenameCalls++;
            return Task.FromResult(new LegacyWebIdentitySourceRenameResult(
                LegacyWebIdentitySourceRenameStatus.Renamed));
        }

        internal DbBackedLegacyWebIdentityRow RequiredRow(string partition, string accountId) =>
            _snapshots.Single(snapshot => snapshot.SourcePartition == partition).Rows
                .Single(row => row.LegacyAccountId == accountId);

        private sealed class MutableSnapshot(
            string sourcePartition,
            long sourceVersion,
            string highWatermark,
            List<DbBackedLegacyWebIdentityRow> rows)
        {
            internal string SourcePartition { get; } = sourcePartition;

            internal long SourceVersion { get; set; } = sourceVersion;

            internal string HighWatermark { get; } = highWatermark;

            internal List<DbBackedLegacyWebIdentityRow> Rows { get; } = rows;
        }
    }

    private sealed class SqliteLegacyStore : IDbBackedLegacyWebIdentityStore, IAsyncDisposable
    {
        private readonly string _path;
        private readonly string _connectionString;

        private SqliteLegacyStore(string path)
        {
            _path = path;
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                DefaultTimeout = 30,
                Pooling = false,
            }.ToString();
        }

        internal static async Task<SqliteLegacyStore> CreateAsync(
            params DbBackedLegacyWebIdentitySnapshot[] snapshots)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"harborline-legacy-store-{Guid.NewGuid():N}.db");
            var store = new SqliteLegacyStore(path);
            await using var connection = await store.OpenAsync(CancellationToken.None);
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE legacy_snapshots (
                        source_partition TEXT NOT NULL PRIMARY KEY,
                        source_version INTEGER NOT NULL,
                        high_watermark TEXT NOT NULL
                    );
                    CREATE TABLE legacy_accounts (
                        source_partition TEXT NOT NULL,
                        legacy_account_id TEXT NOT NULL,
                        normalized_username TEXT NOT NULL,
                        owner_version INTEGER NOT NULL,
                        has_credential INTEGER NOT NULL,
                        is_uncredentialed_invite INTEGER NOT NULL,
                        audit_correlation_id TEXT NOT NULL,
                        PRIMARY KEY (source_partition, legacy_account_id),
                        FOREIGN KEY (source_partition) REFERENCES legacy_snapshots(source_partition)
                    );
                    CREATE TABLE legacy_rename_replays (
                        idempotency_key TEXT NOT NULL PRIMARY KEY,
                        command_fingerprint TEXT NOT NULL
                    );
                    """;
                await schema.ExecuteNonQueryAsync();
            }

            foreach (var snapshot in snapshots)
            {
                await using (var insertSnapshot = connection.CreateCommand())
                {
                    insertSnapshot.CommandText = """
                        INSERT INTO legacy_snapshots (source_partition, source_version, high_watermark)
                        VALUES ($partition, $version, $watermark);
                        """;
                    insertSnapshot.Parameters.AddWithValue("$partition", snapshot.SourcePartition);
                    insertSnapshot.Parameters.AddWithValue("$version", snapshot.SourceVersion);
                    insertSnapshot.Parameters.AddWithValue("$watermark", snapshot.HighWatermark);
                    await insertSnapshot.ExecuteNonQueryAsync();
                }

                foreach (var row in snapshot.Rows)
                {
                    await using var insertRow = connection.CreateCommand();
                    insertRow.CommandText = """
                        INSERT INTO legacy_accounts (
                            source_partition,
                            legacy_account_id,
                            normalized_username,
                            owner_version,
                            has_credential,
                            is_uncredentialed_invite,
                            audit_correlation_id)
                        VALUES ($partition, $account, $username, $ownerVersion,
                            $credential, $invite, $audit);
                        """;
                    insertRow.Parameters.AddWithValue("$partition", snapshot.SourcePartition);
                    insertRow.Parameters.AddWithValue("$account", row.LegacyAccountId);
                    insertRow.Parameters.AddWithValue(
                        "$username",
                        LegacyWebIdentityInventoryReader.NormalizeUsername(row.Username));
                    insertRow.Parameters.AddWithValue("$ownerVersion", row.OwnerVersion);
                    insertRow.Parameters.AddWithValue("$credential", row.HasCredential);
                    insertRow.Parameters.AddWithValue("$invite", row.IsUncredentialedInvite);
                    insertRow.Parameters.AddWithValue("$audit", row.AuditCorrelationId);
                    await insertRow.ExecuteNonQueryAsync();
                }
            }

            return store;
        }

        public async Task<IReadOnlyList<DbBackedLegacyWebIdentitySnapshot>> ReadSnapshotsAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    snapshot.source_partition,
                    snapshot.source_version,
                    snapshot.high_watermark,
                    account.legacy_account_id,
                    account.normalized_username,
                    account.owner_version,
                    account.has_credential,
                    account.is_uncredentialed_invite,
                    account.audit_correlation_id
                FROM legacy_snapshots AS snapshot
                LEFT JOIN legacy_accounts AS account
                    ON account.source_partition = snapshot.source_partition
                ORDER BY snapshot.source_partition, account.legacy_account_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var snapshots = new Dictionary<string, SnapshotBuilder>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var partition = reader.GetString(0);
                if (!snapshots.TryGetValue(partition, out var snapshot))
                {
                    snapshot = new SnapshotBuilder(reader.GetInt64(1), reader.GetString(2));
                    snapshots.Add(partition, snapshot);
                }
                if (!reader.IsDBNull(3))
                {
                    snapshot.Rows.Add(new DbBackedLegacyWebIdentityRow(
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt64(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.GetString(8)));
                }
            }

            return snapshots.Select(pair => new DbBackedLegacyWebIdentitySnapshot(
                    pair.Key,
                    pair.Value.SourceVersion,
                    pair.Value.HighWatermark,
                    pair.Value.Rows))
                .ToArray();
        }

        public async Task<LegacyWebIdentitySourceRenameResult> RenameAsync(
            LegacyWebAccountCompositeKey key,
            string normalizedUsername,
            long expectedOwnerVersion,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                key.SourceKind,
                LegacyWebIdentityInventoryReader.DatabaseSourceKind,
                StringComparison.Ordinal))
            {
                return new LegacyWebIdentitySourceRenameResult(
                    LegacyWebIdentitySourceRenameStatus.SourceNotFound);
            }

            var commandFingerprint = InstallationAuditIntegrity.Hash(
                key.CanonicalValue,
                normalizedUsername,
                expectedOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(
                System.Data.IsolationLevel.Serializable,
                deferred: false);

            await using (var replay = connection.CreateCommand())
            {
                replay.Transaction = transaction;
                replay.CommandText = """
                    SELECT command_fingerprint
                    FROM legacy_rename_replays
                    WHERE idempotency_key = $idempotencyKey;
                    """;
                replay.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
                var replayFingerprint = (string?)await replay.ExecuteScalarAsync(cancellationToken);
                if (replayFingerprint is not null)
                {
                    return new LegacyWebIdentitySourceRenameResult(
                        string.Equals(replayFingerprint, commandFingerprint, StringComparison.Ordinal)
                            ? LegacyWebIdentitySourceRenameStatus.IdempotentReplay
                            : LegacyWebIdentitySourceRenameStatus.ChangedReplay);
                }
            }

            long? currentOwnerVersion;
            await using (var source = connection.CreateCommand())
            {
                source.Transaction = transaction;
                source.CommandText = """
                    SELECT owner_version
                    FROM legacy_accounts
                    WHERE source_partition = $partition
                        AND legacy_account_id = $account;
                    """;
                source.Parameters.AddWithValue("$partition", key.SourcePartition);
                source.Parameters.AddWithValue("$account", key.LegacyAccountId);
                currentOwnerVersion = (long?)await source.ExecuteScalarAsync(cancellationToken);
            }
            if (currentOwnerVersion is null)
            {
                return new LegacyWebIdentitySourceRenameResult(
                    LegacyWebIdentitySourceRenameStatus.SourceNotFound);
            }
            if (currentOwnerVersion != expectedOwnerVersion)
            {
                return new LegacyWebIdentitySourceRenameResult(
                    LegacyWebIdentitySourceRenameStatus.SourceVersionStale);
            }

            await using (var target = connection.CreateCommand())
            {
                target.Transaction = transaction;
                target.CommandText = """
                    SELECT 1
                    FROM legacy_accounts
                    WHERE normalized_username = $username
                        AND NOT (
                            source_partition = $partition
                            AND legacy_account_id = $account)
                    LIMIT 1;
                    """;
                target.Parameters.AddWithValue("$username", normalizedUsername);
                target.Parameters.AddWithValue("$partition", key.SourcePartition);
                target.Parameters.AddWithValue("$account", key.LegacyAccountId);
                if (await target.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    return new LegacyWebIdentitySourceRenameResult(
                        LegacyWebIdentitySourceRenameStatus.TargetUsernameCollision);
                }
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE legacy_accounts
                    SET normalized_username = $username,
                        owner_version = owner_version + 1
                    WHERE source_partition = $partition
                        AND legacy_account_id = $account
                        AND owner_version = $ownerVersion;
                    """;
                update.Parameters.AddWithValue("$username", normalizedUsername);
                update.Parameters.AddWithValue("$partition", key.SourcePartition);
                update.Parameters.AddWithValue("$account", key.LegacyAccountId);
                update.Parameters.AddWithValue("$ownerVersion", expectedOwnerVersion);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    return new LegacyWebIdentitySourceRenameResult(
                        LegacyWebIdentitySourceRenameStatus.SourceVersionStale);
                }
            }

            await using (var replay = connection.CreateCommand())
            {
                replay.Transaction = transaction;
                replay.CommandText = """
                    INSERT INTO legacy_rename_replays (idempotency_key, command_fingerprint)
                    VALUES ($idempotencyKey, $fingerprint);
                    """;
                replay.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
                replay.Parameters.AddWithValue("$fingerprint", commandFingerprint);
                await replay.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new LegacyWebIdentitySourceRenameResult(
                LegacyWebIdentitySourceRenameStatus.Renamed);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            return ValueTask.CompletedTask;
        }

        private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        private sealed record SnapshotBuilder(long SourceVersion, string HighWatermark)
        {
            internal List<DbBackedLegacyWebIdentityRow> Rows { get; } = [];
        }
    }

    private sealed class FakeRecoveryAuthority(InstallationRecoveryPrincipalEvidence evidence)
        : IInstallationRecoveryPrincipalAuthority
    {
        internal int RequireCalls { get; private set; }

        public Task<InstallationRecoveryPrincipalEvidence> RequireAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireCalls++;
            return Task.FromResult(evidence);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(
            string path,
            IdentityContextFactory factory,
            FixedTimeProvider timeProvider,
            InstallationRecoveryPrincipalEvidence recoveryEvidence)
        {
            Path = path;
            Factory = factory;
            TimeProvider = timeProvider;
            RecoveryEvidence = recoveryEvidence;
        }

        internal string Path { get; }

        internal IdentityContextFactory Factory { get; }

        internal FixedTimeProvider TimeProvider { get; }

        internal InstallationRecoveryPrincipalEvidence RecoveryEvidence { get; }

        internal static async Task<TestDatabase> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"harborline-legacy-migration-{Guid.NewGuid():N}.db");
            var factory = new IdentityContextFactory(path);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }
            return new TestDatabase(
                path,
                factory,
                new FixedTimeProvider(FixedNow),
                new InstallationRecoveryPrincipalEvidence(
                    "os-recovery-test",
                    1,
                    string.Join(":", Enumerable.Repeat("AB", 32))));
        }

        internal static async Task<TestDatabase> CreateWithFounderAsync()
        {
            var database = await CreateAsync();
            var bootstrap = new InstallationFounderBootstrapService(
                database.Factory,
                database.TimeProvider);
            await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "audit-root@example.test",
                "$argon2id$v=19$m=19456,t=2,p=1$"
                    + Convert.ToBase64String(new byte[16]) + "$"
                    + Convert.ToBase64String(new byte[32]),
                Guid.NewGuid().ToString("N"),
                database.RecoveryEvidence.RootPublicKeyFingerprint,
                "legacy-migration-audit-root"));
            return database;
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

    internal sealed class IdentityContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }

    private sealed class FailOnceIdentityContextFactory(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> inner,
        int failOnCall) : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        private int _calls;
        private bool _failed;

        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            _calls++;
            if (!_failed && _calls == failOnCall)
            {
                _failed = true;
                throw new SimulatedInterruptionException();
            }

            return inner.CreateDbContext();
        }
    }

    private sealed class SimulatedInterruptionException : Exception;
}
