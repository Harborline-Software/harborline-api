using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.DependencyInjection;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// #1378 M-1 — the durable subject-erasure stores (<see cref="NodeEfSubjectErasureRegistry"/> /
/// <see cref="NodeEfSubjectTombstoneStore"/>) MUST make a crypto-shred SURVIVE a process restart, and the
/// <see cref="ServiceCollectionExtensions.RequireDurableErasureStores"/> composition-root gate MUST refuse to
/// boot a host that left the restart-volatile in-memory defaults wired behind the live erasure service.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resurrection hole this closes.</b> The per-subject sub-key is <em>derived</em> deterministically
/// (<see cref="RootSeedTenantKeyProvider.DeriveSubjectKeyAsync"/>); a shred is realized by the erasure REGISTRY
/// failing that derivation closed. With the in-memory registry, a restart forgets the shred ⇒ the sub-key
/// re-derives ⇒ the durable ciphertext becomes decryptable again (GDPR Art-17). The durable EF stores persist the
/// shred + tombstone to the SAME SQLCipher file as the index, so the shred holds across a context/store
/// reconstruction. A "restart" here is a fresh registry instance over a fresh context factory pointed at the same
/// on-disk encrypted file (EF pools cleared) — the registry holds no in-process state, so this faithfully models
/// process recycle.
/// </para>
/// </remarks>
public sealed class DurableSubjectErasureRestartTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly SubjectId Alice = new("subject-alice");
    private static readonly SubjectId Bob = new("subject-bob");

    [Fact(DisplayName = "M-1: a crypto-shred recorded via the durable EF registry SURVIVES a restart — the subject key stays un-derivable and the durable ciphertext stays dark; a non-erased same-tenant subject still derives")]
    public async Task Shred_Survives_Restart_KeyStaysUnDerivable()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var rootSeed = RootSeed(seed: 7);

        // ── BEFORE restart: run a real erasure through the DURABLE stores. ──────────────────────────────────
        {
            var registry = new NodeEfSubjectErasureRegistry(store.Factory, TimeProvider.System);
            var tombstones = new NodeEfSubjectTombstoneStore(store.Factory);
            var keyProvider = new RootSeedTenantKeyProvider(rootSeed);

            // Sanity: both subjects' sub-keys derive BEFORE the shred (the registry is empty).
            await keyProvider.DeriveSubjectKeyAsync(TenantA, Alice, "encrypted-field-aes", registry, CancellationToken.None);
            await keyProvider.DeriveSubjectKeyAsync(TenantA, Bob, "encrypted-field-aes", registry, CancellationToken.None);

            var erasureSvc = new SubjectErasureService(
                registry,
                tombstones,
                new NoopAuditTrail(),
                new Ed25519Signer(KeyPair.Generate()),
                new NoopTenantKeyDestroyer(),
                minimumWindow: TimeSpan.Zero,
                clock: new FixedClock(Now),
                propagators: null);

            var result = await erasureSvc.EraseAsync(ValidRequest(Alice), CancellationToken.None);
            Assert.Equal(SubjectErasureOutcome.Erased, result.Outcome);
        }

        // ── Simulate a process restart: rebuild a fresh store/registry object graph over the SAME on-disk
        //    encrypted file. SearchTestStore's connections are Pooling=False, so the prior block's disposal
        //    already released every in-process handle — no global pool clear needed here (bug-20260702-8012e553).
        await using var afterRestart = SearchTestStore.Reopen(store);
        {
            var registry = new NodeEfSubjectErasureRegistry(afterRestart.Factory, TimeProvider.System);
            var tombstones = new NodeEfSubjectTombstoneStore(afterRestart.Factory);
            var keyProvider = new RootSeedTenantKeyProvider(rootSeed);

            // M-1 ASSERTION 1 — the shred SURVIVED: the registry still reports Alice erased after restart.
            Assert.True(await registry.IsErasedAsync(TenantA, Alice, CancellationToken.None));

            // M-1 ASSERTION 2 — the per-subject key STAYS un-derivable: deriving Alice's sub-key fails closed,
            // so her durable ciphertext can never be decrypted again. This is the resurrection path being closed.
            await Assert.ThrowsAsync<SubjectErasedException>(() =>
                keyProvider.DeriveSubjectKeyAsync(TenantA, Alice, "encrypted-field-aes", registry, CancellationToken.None));

            // M-1 ASSERTION 3 — the tombstone (compliance record) survived too.
            var pseudonym = SubjectPseudonym.Derive(TenantA, Alice);
            var tombstone = await tombstones.FindAsync(TenantA, pseudonym, CancellationToken.None);
            Assert.NotNull(tombstone);
            Assert.Equal(2, tombstone!.ApprovingActors.Count);
            Assert.Equal("erasure-ticket", tombstone.LegalBasis);

            // ISOLATION — a non-erased same-tenant subject is unaffected: Bob's sub-key still derives post-restart.
            var bobKey = await keyProvider.DeriveSubjectKeyAsync(
                TenantA, Bob, "encrypted-field-aes", registry, CancellationToken.None);
            Assert.Equal(32, bobKey.Length);
        }
    }

    [Fact(DisplayName = "M-1 gate: RequireDurableErasureStores PASSES with the durable EF stores and FAILS CLOSED on the restart-volatile in-memory defaults")]
    public void Gate_Passes_With_Durable_Stores_Fails_With_Volatile_Defaults()
    {
        // FAIL CLOSED — the volatile defaults (what AddHarborlineRecoveryCoordinator alone leaves) are refused.
        var volatileServices = new ServiceCollection();
        volatileServices.AddSingleton<IOperationSigner>(new Ed25519Signer(KeyPair.Generate()));
        volatileServices.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(RootSeed(7)));
        volatileServices.AddHarborlineRecoveryCoordinator();
        Assert.Throws<InvalidOperationException>(() => volatileServices.RequireDurableErasureStores());

        // PASSES — registering the durable EF stores BEFORE the coordinator (so its TryAdd keeps ours) clears it.
        var durableServices = new ServiceCollection();
        durableServices.AddSingleton<IOperationSigner>(new Ed25519Signer(KeyPair.Generate()));
        durableServices.AddSingleton<ITenantKeyProvider>(new RootSeedTenantKeyProvider(RootSeed(7)));
        durableServices.AddSingleton<ISubjectErasureRegistry, NodeEfSubjectErasureRegistry>();
        durableServices.AddSingleton<ISubjectTombstoneStore, NodeEfSubjectTombstoneStore>();
        durableServices.AddHarborlineRecoveryCoordinator();
        var returned = durableServices.RequireDurableErasureStores();
        Assert.Same(durableServices, returned);
    }

    private static byte[] RootSeed(byte seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + seed);
        }
        return bytes;
    }

    private static SubjectErasureRequest ValidRequest(SubjectId subject) => new(
        TenantA, subject, Now,
        new[] { new ActorId("captain"), new ActorId("officer") }, "erasure-ticket");

    private sealed class FixedClock : IRecoveryClock
    {
        private readonly DateTimeOffset _instant;
        public FixedClock(DateTimeOffset instant) => _instant = instant;
        public DateTimeOffset UtcNow() => _instant;
    }

    private sealed class NoopAuditTrail : IAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => ValueTask.CompletedTask;

        public async System.Collections.Generic.IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopTenantKeyDestroyer : ITenantKeyDestroyer
    {
        public Task DestroySubjectKeysAsync(TenantId tenant, SubjectId subject, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DestroyTenantKeysAsync(TenantId tenant, CancellationToken ct) => Task.CompletedTask;
    }
}
