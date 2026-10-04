using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackNarrowingCommitReadsetTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000001048");
    private static readonly DateTimeOffset At = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string PackKey = "test.narrowing-readset";
    private const string ContentKey = "role-form";
    private const string Principal = "test-operator";

    [Theory]
    [InlineData(false, "active version")]
    [InlineData(true, "active version")]
    [InlineData(false, "same-key override")]
    [InlineData(true, "same-key override")]
    [InlineData(false, "sibling override")]
    [InlineData(true, "sibling override")]
    [InlineData(false, "deactivated")]
    [InlineData(true, "deactivated")]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    public async Task Suspended_signing_cannot_commit_narrowing_over_changed_admission_premises(bool durable, string change)
    {
        await using var harness = await DurableAuditHarness.CreateAsync();
        await using (var packsContext = harness.Store.CreatePacksContext()) await packsContext.Database.MigrateAsync();
        using var keys = KeyPair.Generate();
        using var nodeSigner = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var suspended = new SuspendedSigner(nodeSigner.Signer);
        var audit = new KernelAuditPackInstallAudit(harness.Trail, suspended,
            NullLogger<KernelAuditPackInstallAudit>.Instance, harness.Outbox);
        IPackInstallMutationStore store = durable
            ? new DurablePackInstallStore(harness.Store.PacksFactory, audit)
            : new InMemoryPackInstallStore();
        Commit(store, keys, "1.0.0", "{\"roles\":[\"A\",\"B\"]}");
        store.Activate(Tenant, PackKey, "1.0.0");
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()),
            store, new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.AllowGate());
        var context = new PackInstallContext(Tenant,
            new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, keys.PrincipalId, 1, TrustRootStatus.Current)]),
            PackRevocationList.Empty, At, TimeSpan.FromDays(30), Principal: Principal);
        var decision = TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, Principal, At);
        var operation = installer.NarrowAsync(context, PackKey, ContentKey, JsonNode.Parse("{\"roles\":[\"A\"]}")!, decision);
        Assert.False(operation.IsCompleted);
        await suspended.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (change == "active version")
            {
                Commit(store, keys, "2.0.0", "{\"roles\":[\"B\",\"C\"]}");
                store.Activate(Tenant, PackKey, "2.0.0");
            }
            else if (change is "same-key override" or "sibling override")
                store.SaveOverride(Tenant, PackKey, new PackTenantOverride(
                    change == "same-key override" ? ContentKey : "sibling-form", JsonNode.Parse("{\"roles\":[\"B\"]}")!));
            else if (change == "deactivated") store.Deactivate(Tenant, PackKey, "1.0.0");
            suspended.Release.TrySetResult();
            var result = await operation;
            Assert.Equal(change == "none", result.Recorded);
            if (change != "none") Assert.Equal("pack.install.refused.installed_state_changed", result.RefusalCode);
            var overrides = store.GetOverrides(Tenant, PackKey);
            if (change == "none")
                Assert.Equal("{\"roles\":[\"A\"]}", Assert.Single(overrides).OverlayPatch.ToJsonString());
            else if (change is "same-key override" or "sibling override")
            {
                var persisted = Assert.Single(overrides);
                Assert.Equal(change == "same-key override" ? ContentKey : "sibling-form", persisted.ContentKey);
                Assert.Equal("{\"roles\":[\"B\"]}", persisted.OverlayPatch.ToJsonString());
            }
            else Assert.Empty(overrides);
            Assert.Equal(change == "active version" ? "2.0.0" : change == "deactivated" ? null : "1.0.0",
                store.GetActive(Tenant, PackKey)?.Version);
            await using var db = harness.Store.CreateContext();
            Assert.Equal(durable && change == "none" ? 1 : 0,
                await db.AuditOutbox.CountAsync(row => row.EventType == "PackComposerInstall"));
            var entries = audit.Query(Tenant);
            Assert.Equal(change == "none" ? "Narrowed" : "Refused", Assert.Single(entries).Action.ToString());
        }
        finally { suspended.Release.TrySetResult(); }
    }

    private static void Commit(IPackInstallMutationStore store, KeyPair keys, string version, string seed)
    {
        var pack = new InstalledPack(PackKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft,
            [new PackSeedItem(ContentKey, PackContentKind.FormDefinition, version, seed, Cid.FromBytes(Encoding.UTF8.GetBytes(seed)))],
            new Dictionary<string, int>(), At, keys.PrincipalId, 1, TrustScope.OwnRoster, []);
        store.Commit(new PackInstallTransaction(Tenant, pack, new PackInstallWatermark(PackKey, version, new Dictionary<string, int>()), []));
    }

    private sealed class SuspendedSigner(IOperationSigner inner) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await inner.SignAsync(payload, issuedAt, nonce, ct);
        }
    }
}
