using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CryptoShred;

public sealed class StoredTenantKeyProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public async Task TenantAndSubjectKeys_AreStoredAndNotRootDerived()
    {
        var hierarchyRoot = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        var store = new InMemoryTenantKeyStore();
        var firstReplica = new StoredTenantKeyProvider(store, hierarchyRoot);
        var tenant = new TenantId("tenant-043");
        var subject = new SubjectId("subject-043");
        var erasure = new InMemorySubjectErasureRegistry();

        var tenantKey = await firstReplica.DeriveKeyAsync(
            tenant,
            "encrypted-field-aes",
            CancellationToken.None);
        var subjectKey = await firstReplica.DeriveSubjectKeyAsync(
            tenant,
            subject,
            "encrypted-field-aes",
            erasure,
            CancellationToken.None);

        var afterRestart = new StoredTenantKeyProvider(store, hierarchyRoot);
        Assert.Equal(
            tenantKey.ToArray(),
            (await afterRestart.DeriveKeyAsync(tenant, "encrypted-field-aes", CancellationToken.None)).ToArray());
        Assert.Equal(
            subjectKey.ToArray(),
            (await afterRestart.DeriveSubjectKeyAsync(
                tenant,
                subject,
                "encrypted-field-aes",
                erasure,
                CancellationToken.None)).ToArray());
        Assert.NotEqual(
            "7856CC7236FCBD57688C2160CA51304FB2E11A96D34BB5E0773996B95E79D1CC",
            Convert.ToHexString(tenantKey.Span));
        Assert.NotEqual(
            "31B330D8DEA50DA58AA2A9A53FE3108BB6ECAB441DCDC14238F37B6B6B4D5632",
            Convert.ToHexString(subjectKey.Span));
    }

    [Fact]
    public async Task Shred_MakesCiphertextUnreadableOnSecondReplica()
    {
        var store = new InMemoryTenantKeyStore();
        var hierarchyRoot = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
        var firstReplicaKeys = new StoredTenantKeyProvider(store, hierarchyRoot);
        var secondReplicaKeys = new StoredTenantKeyProvider(store, hierarchyRoot);
        var firstReplicaErasure = new InMemorySubjectErasureRegistry();
        var secondReplicaErasure = new InMemorySubjectErasureRegistry();
        var tenant = new TenantId("tenant-replica");
        var subject = new SubjectId("subject-replica");
        var ciphertext = await new SubjectKeyFieldEncryptor(secondReplicaKeys, secondReplicaErasure)
            .EncryptForSubjectAsync("replicated cleartext"u8.ToArray(), tenant, subject, CancellationToken.None);
        var capability = new FixedDecryptCapability(
            "ticket-055-read",
            ActorId.System,
            tenant,
            Now.AddHours(1));
        var replicaReader = new SubjectKeyFieldDecryptor(
            secondReplicaKeys,
            secondReplicaErasure,
            new FixedClock(Now));
        Assert.Equal(
            "replicated cleartext"u8.ToArray(),
            (await replicaReader.DecryptForSubjectAsync(
                ciphertext,
                capability,
                tenant,
                subject,
                CancellationToken.None)).ToArray());
        var erasure = new SubjectErasureService(
            firstReplicaErasure,
            new InMemorySubjectTombstoneStore(),
            new NoopAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()),
            keyDestroyer: firstReplicaKeys,
            minimumWindow: TimeSpan.Zero,
            clock: new FixedClock(Now));

        var result = await erasure.EraseAsync(
            new SubjectErasureRequest(
                tenant,
                subject,
                Now,
                [new ActorId("captain"), new ActorId("officer")],
                "ticket-055"),
            CancellationToken.None);

        Assert.Equal(SubjectErasureOutcome.Erased, result.Outcome);
        await Assert.ThrowsAsync<FieldDecryptionDeniedException>(() =>
            replicaReader.DecryptForSubjectAsync(
                ciphertext,
                capability,
                tenant,
                subject,
                CancellationToken.None));
    }

    [Fact]
    public async Task ActiveLegalHold_RefusesShredAndPreservesStoredKey()
    {
        var tenant = new TenantId("tenant-held");
        var subject = new SubjectId("subject-held");
        var erasureRegistry = new InMemorySubjectErasureRegistry();
        var keys = new StoredTenantKeyProvider(new InMemoryTenantKeyStore(), new byte[32]);
        var ciphertext = await new SubjectKeyFieldEncryptor(keys, erasureRegistry)
            .EncryptForSubjectAsync("held cleartext"u8.ToArray(), tenant, subject, CancellationToken.None);
        var holdStore = new InMemoryLegalHoldStore();
        await holdStore.AppendHoldAsync(new LegalHoldEntry(
            LegalHoldId.New(),
            tenant,
            HeldRef.ForSubject(subject),
            "matter-055",
            new ActorId("counsel"),
            Now));
        var service = new SubjectErasureService(
            erasureRegistry,
            new InMemorySubjectTombstoneStore(),
            new NoopAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()),
            keys,
            minimumWindow: TimeSpan.Zero,
            clock: new FixedClock(Now),
            holds: new LegalHoldRegistry(holdStore));

        var result = await service.EraseAsync(
            new SubjectErasureRequest(
                tenant,
                subject,
                Now,
                [new ActorId("captain"), new ActorId("officer")],
                "ticket-055"),
            CancellationToken.None);

        Assert.Equal(SubjectErasureOutcome.BlockedByLegalHold, result.Outcome);
        var plaintext = await new SubjectKeyFieldDecryptor(
                keys,
                erasureRegistry,
                new FixedClock(Now))
            .DecryptForSubjectAsync(
                ciphertext,
                new FixedDecryptCapability(
                    "ticket-055-held-read",
                    ActorId.System,
                    tenant,
                    Now.AddHours(1)),
                tenant,
                subject,
                CancellationToken.None);
        Assert.Equal("held cleartext"u8.ToArray(), plaintext.ToArray());
    }

    [Fact]
    public async Task LegacyDerivedCiphertext_MigratesToStoredKeyWithoutLosingAccess()
    {
        var rootSeed = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        var store = new InMemoryTenantKeyStore();
        var tenant = new TenantId("tenant-043");
        var subject = new SubjectId("subject-043");
        var registry = new InMemorySubjectErasureRegistry();
        var legacyCiphertext = new EncryptedField(
            Convert.FromHexString("1087FE1CA05F4223EEC8A43715134C85B77A65DCA8C7424D5F71575F039D7765"),
            Convert.FromHexString("000102030405060708090A0B"),
            keyVersion: 1,
            CryptoSuite.AesGcm256Hkdf_v1);
        var capability = new FixedDecryptCapability(
            "ticket-055-legacy-read",
            ActorId.System,
            tenant,
            Now.AddHours(1));
        var upgradingProvider = new StoredTenantKeyProvider(
            store,
            rootSeed,
            legacyProvider: new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed));

        var firstRead = await new SubjectKeyFieldDecryptor(
                upgradingProvider,
                registry,
                new FixedClock(Now))
            .DecryptForSubjectAsync(
                legacyCiphertext,
                capability,
                tenant,
                subject,
                CancellationToken.None);

        var afterMigration = new StoredTenantKeyProvider(store, rootSeed);
        var restartedRead = await new SubjectKeyFieldDecryptor(
                afterMigration,
                registry,
                new FixedClock(Now))
            .DecryptForSubjectAsync(
                legacyCiphertext,
                capability,
                tenant,
                subject,
                CancellationToken.None);
        Assert.Equal("legacy cleartext"u8.ToArray(), firstRead.ToArray());
        Assert.Equal("legacy cleartext"u8.ToArray(), restartedRead.ToArray());
    }

    [Fact]
    public async Task ProductionComposition_PersistsStoredKeysAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket-055-{Guid.NewGuid():N}");
        try
        {
            var root = Enumerable.Range(96, 32).Select(value => (byte)value).ToArray();
            var tenant = new TenantId("tenant-production");
            var subject = new SubjectId("subject-production");
            var registry = new InMemorySubjectErasureRegistry();
            var firstServices = new ServiceCollection();
            firstServices.ConfigureStoredTenantKeys(directory, root);
            await using var first = firstServices.BuildServiceProvider();
            var firstKeys = first.GetRequiredService<ITenantKeyProvider>();
            Assert.Same(firstKeys, first.GetRequiredService<ITenantKeyDestroyer>());
            var ciphertext = await new SubjectKeyFieldEncryptor(firstKeys, registry)
                .EncryptForSubjectAsync("durable key"u8.ToArray(), tenant, subject, CancellationToken.None);

            var restartedServices = new ServiceCollection();
            restartedServices.ConfigureStoredTenantKeys(directory, root);
            await using var restarted = restartedServices.BuildServiceProvider();
            var plaintext = await new SubjectKeyFieldDecryptor(
                    restarted.GetRequiredService<ITenantKeyProvider>(),
                    registry,
                    new FixedClock(Now))
                .DecryptForSubjectAsync(
                    ciphertext,
                    new FixedDecryptCapability(
                        "ticket-055-production-read",
                        ActorId.System,
                        tenant,
                        Now.AddHours(1)),
                    tenant,
                    subject,
                    CancellationToken.None);

            Assert.Equal("durable key"u8.ToArray(), plaintext.ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset instant) : IRecoveryClock
    {
        public DateTimeOffset UtcNow() => instant;
    }

    private sealed class NoopAuditTrail : IAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
