using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Storage;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Tests.Search;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;
using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackSameVersionRetryTests(ITestOutputHelper output)
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000001098");
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const string PackKey = "test.t1098-a3";
    private const string Version = "1.0.0";
    private const string Operator = "test-operator";

    // T-1098 A3/Q34: an accepted identical retry must preserve the actual Active lifecycle.
    [Fact]
    public async Task Identical_retry_of_active_version_preserves_lifecycle()
    {
        using var keys = KeyPair.Generate();
        using var nodeSigner = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var packs = origin.Store.CreatePacksContext())
            await packs.Database.MigrateAsync();

        var bytes = await ExportAsync(keys);
        var artifactHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        output.WriteLine($"artifact: bytes={bytes.Length}, sha256={artifactHash}");

        Observation before;
        Observation immediate;
        PackInstallOutcome retry;
        // The origin owns the file. Dispose only non-owning operation/reopen harnesses until observations finish.
        await using (var operation = origin.Reopen())
        {
            var (store, installer) = Compose(operation, nodeSigner);
            PlatformPackTestPreload.Activate(store, Tenant);
            var context = Context(keys);

            var installed = await installer.InstallAsync(bytes, context);
            Assert.True(installed.Installed, string.Join(",", installed.RefusalCodes));
            Assert.Equal("test.t1098-a3", installed.PackKey);
            Assert.Equal("1.0.0", installed.Version);

            var activated = await installer.ActivateAsync(context, PackKey, Version);
            Assert.True(activated.Activated, activated.Error);

            before = await ObserveAsync(operation, store);
            WriteObservation("before-retry", before);
            Assert.Equal<int?>(1, before.LifecycleColumn);
            Assert.Equal("Active", before.VersionLifecycle);
            Assert.Equal("1.0.0", before.StoredVersion);
            Assert.Equal("Active", before.ActiveLifecycle);
            Assert.Equal("1.0.0", before.ActiveVersion);

            retry = await installer.InstallAsync(bytes, context);
            output.WriteLine("retry-outcome: " + JsonSerializer.Serialize(new
            {
                retry.Installed,
                Action = retry.Action.ToString(),
                retry.PackKey,
                retry.Version,
                retry.BrokeGlass,
                retry.RefusalCodes,
                PreviewVerdict = retry.Preview.Verdict.ToString(),
            }));
            output.WriteLine("retry-artifact-sha256: " + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            immediate = await ObserveAsync(operation, store);
            WriteObservation("immediate-after-retry", immediate);
        }

        Observation reopened;
        await using (var restart = origin.Reopen())
        {
            var store = new DurablePackInstallStore(restart.Store.PacksFactory);
            reopened = await ObserveAsync(restart, store);
            WriteObservation("reopened-after-retry", reopened);
        }

        // All retry and reopened evidence precedes the desired-preservation assertions, including a semantic RED.
        Assert.True(retry.Installed, string.Join(",", retry.RefusalCodes));
        Assert.Empty(retry.RefusalCodes);
        Assert.Equal("test.t1098-a3", retry.PackKey);
        Assert.Equal("1.0.0", retry.Version);
        Assert.Equal(artifactHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal<int?>(1, immediate.LifecycleColumn);
        Assert.Equal("Active", immediate.VersionLifecycle);
        Assert.Equal("1.0.0", immediate.StoredVersion);
        Assert.Equal("Active", immediate.ActiveLifecycle);
        Assert.Equal("1.0.0", immediate.ActiveVersion);
        Assert.Equal<int?>(1, reopened.LifecycleColumn);
        Assert.Equal("Active", reopened.VersionLifecycle);
        Assert.Equal("1.0.0", reopened.StoredVersion);
        Assert.Equal("Active", reopened.ActiveLifecycle);
        Assert.Equal("1.0.0", reopened.ActiveVersion);
        Assert.Equal(before.PayloadJson, immediate.PayloadJson);
        Assert.Equal(before.PayloadJson, reopened.PayloadJson);
        Assert.Equal(before.WatermarkVersion, immediate.WatermarkVersion);
        Assert.Equal(before.WatermarkVersion, reopened.WatermarkVersion);
        Assert.Equal(before.FloorsJson, immediate.FloorsJson);
        Assert.Equal(before.FloorsJson, reopened.FloorsJson);
        Assert.Equal(before.ApiWatermarkJson, immediate.ApiWatermarkJson);
        Assert.Equal(before.ApiWatermarkJson, reopened.ApiWatermarkJson);
        Assert.Equal(before.DurableOverridesJson, immediate.DurableOverridesJson);
        Assert.Equal(before.DurableOverridesJson, reopened.DurableOverridesJson);
        Assert.Equal(before.ApiOverridesJson, immediate.ApiOverridesJson);
        Assert.Equal(before.ApiOverridesJson, reopened.ApiOverridesJson);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Active")]
    [InlineData("Inactive")]
    [InlineData("Superseded")]
    public async Task Durable_retry_preserves_all_lifecycles_and_business_rows_after_reopen(string lifecycle)
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var (store, installer) = Compose(origin, node);
        PlatformPackTestPreload.Activate(store, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        if (lifecycle != "Draft") Assert.True((await installer.ActivateAsync(Context(keys), PackKey, Version)).Activated);
        if (lifecycle == "Inactive") Assert.True((await installer.DeactivateAsync(Context(keys), PackKey, Version)).Deactivated);
        if (lifecycle == "Superseded")
        {
            Assert.True((await installer.InstallAsync(await NextVersionAsync(bytes, keys), Context(keys))).Installed);
            Assert.True((await installer.ActivateAsync(Context(keys), PackKey, "2.0.0")).Activated);
        }
        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.t1098-form", JsonNode.Parse("""{"witness":"preserve"}""")!));
        store.RecordKeyOwnership(Tenant, "fixtures.t1098-form", PackKey);
        Assert.Equal(lifecycle, store.GetVersion(Tenant, PackKey, Version)!.Lifecycle.ToString());
        var before = await DurableSnapshotAsync(origin, includeAudit: false);
        var auditCount = (await OutboxActionsAsync(origin)).Length;
        var retry = await installer.InstallAsync(bytes, Context(keys));
        Assert.True(retry.Installed);
        Assert.Equal("Unchanged", retry.Action.ToString());
        Assert.False(retry.BrokeGlass);
        Assert.Equal(before, await DurableSnapshotAsync(origin, includeAudit: false));
        Assert.Equal(auditCount + 1, (await OutboxActionsAsync(origin)).Length);
        Assert.Equal(1, (await OutboxActionsAsync(origin)).Count(action => action == "Unchanged"));
    }

    [Fact]
    public async Task Real_default_joined_body_poison_prevents_commit_and_leaves_release_with_owner()
    {
        using var keys = KeyPair.Generate();
        var innerStore = new InMemoryPackInstallStore();
        var engine = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), innerStore,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        IPackInstaller unsupported = new UnsupportedJoinedInstaller(engine);
        var durable = new CountedUnit();
        using var transaction = new PackProjectionTransaction();
        var sibling = 1;
        var cleaned = 0;
        var delivered = 0;
        transaction.Stage(new object(), () => { sibling = 2; return () => sibling = 1; });
        transaction.Finally(() => cleaned++);
        transaction.Durable(() => durable);
        transaction.AfterCommit(() => { delivered++; return Task.CompletedTask; });
        var original = Assert.Throws<InvalidOperationException>(() => { _ = unsupported.InstallAsync(ReadOnlyMemory<byte>.Empty, Context(keys), transaction); });
        var refused = Assert.Throws<InvalidOperationException>(transaction.Commit);
        Assert.Same(original, refused.InnerException);
        Assert.Equal(0, durable.Commits);
        Assert.Equal((2, 0, 0), (sibling, cleaned, delivered));
        transaction.Dispose();
        Assert.Null(await transaction.ReactAsync());
        Assert.Equal((1, 1, 0, 1), (sibling, cleaned, delivered, durable.Disposals));
        Assert.True(transaction.Diagnostics!.ResourceReleaseConfirmed);
        Assert.False(transaction.Diagnostics.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Joined_supported_enlist_or_create_fault_poison_prevents_catch_then_commit(bool create)
    {
        using var transaction = new PackProjectionTransaction();
        var failure = new IOException("joined participant preparation");
        var sibling = "before";
        transaction.Stage(new object(), () => { sibling = "staged"; return () => sibling = "before"; });
        transaction.BeginJoinedInstall();
        if (create)
            Assert.Same(failure, Assert.Throws<IOException>(() => transaction.Durable<CountedUnit>(() => throw failure)));
        else
            Assert.Same(failure, Assert.Throws<IOException>(() => transaction.Enlist(new ThrowingParticipant(failure))));
        var refused = Assert.Throws<InvalidOperationException>(transaction.Commit);
        Assert.Equal("The projection contains a failed joined install and cannot commit or stage more work.", refused.Message);
        Assert.Same(failure, refused.InnerException);
        Assert.Equal("staged", sibling);
        transaction.Dispose();
        Assert.Null(await transaction.ReactAsync());
        Assert.Equal("before", sibling);
    }

    [Fact]
    public async Task First_optional_failure_cause_is_retained_even_when_it_was_null()
    {
        using var transaction = new PackProjectionTransaction();
        transaction.MarkFailed();
        transaction.MarkFailed(new IOException("later cause"));
        Assert.Null(Assert.Throws<InvalidOperationException>(transaction.Commit).InnerException);
        transaction.Dispose();
        Assert.Null(await transaction.ReactAsync());
    }

    private sealed class ThrowingParticipant(IOException failure) : IPackProjectionParticipant
    {
        public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () => throw failure);
    }

    [Fact]
    public async Task First_actual_durable_commit_throw_is_preserved_and_second_call_never_reenters_port()
    {
        var failure = new IOException("first durable Commit");
        var durable = new CountedUnit { CommitFailure = failure };
        using var transaction = new PackProjectionTransaction();
        var sibling = "before";
        var delivered = 0;
        transaction.Stage(new object(), () => { sibling = "staged"; return () => sibling = "before"; });
        transaction.Durable(() => durable);
        transaction.AfterCommit(() => { delivered++; return Task.CompletedTask; });
        Assert.Same(failure, Assert.Throws<IOException>(transaction.Commit));
        Assert.Equal(1, durable.Commits);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(transaction.Commit).InnerException);
        Assert.Equal(1, durable.Commits);
        Assert.Equal("staged", sibling);
        Assert.Equal(0, durable.Disposals);
        transaction.Dispose();
        Assert.Null(await transaction.ReactAsync());
        Assert.Equal("before", sibling);
        Assert.Equal((1, 1, 0), (durable.Commits, durable.Disposals, delivered));
    }

    [Fact]
    public async Task Protected_cleanup_attempts_all_in_order_and_uncertain_release_retires_delivery()
    {
        var first = new IOException("rollback");
        var second = new IOException("cleanup");
        var third = new IOException("durable disposal");
        var seen = new List<string>();
        using var transaction = new PackProjectionTransaction();
        transaction.MarkFailed();
        Assert.Throws<InvalidOperationException>(() => transaction.AfterCommit(() => Task.CompletedTask));
        transaction.Finally(() => { seen.Add("cleanup-1"); });
        transaction.Finally(() => { seen.Add("cleanup-2"); throw second; });
        transaction.AfterRelease(() => { seen.Add("refusal"); return Task.CompletedTask; });
        transaction.Dispose();
        var aggregate = await transaction.ReactAsync();
        Assert.Equal(new[] { "cleanup-2", "cleanup-1", "refusal" }, seen);
        Assert.Same(second, Assert.Single(aggregate!.InnerExceptions));
        var snapshot = transaction.Diagnostics;
        Assert.Null(await transaction.ReactAsync());
        Assert.Same(snapshot, transaction.Diagnostics);

        using var uncertain = new PackProjectionTransaction();
        uncertain.BeginJoinedInstall();
        uncertain.Stage(new object(), () => () => { seen.Add("rollback"); throw first; });
        uncertain.Finally(() => { seen.Add("cleanup"); throw second; });
        uncertain.Durable(() => new CountedUnit { DisposeFailure = third });
        var delivered = 0;
        uncertain.AfterRelease(() => { delivered++; return Task.CompletedTask; });
        uncertain.MarkFailed(new IOException("primary"));
        uncertain.Dispose();
        var diagnostics = await uncertain.ReactAsync();
        Assert.Equal(new Exception[] { first, second, third }, diagnostics!.InnerExceptions);
        Assert.Equal(0, delivered);
        Assert.False(uncertain.Diagnostics!.ResourceReleaseConfirmed);
        Assert.Equal(new[] { "Rollback", "Cleanup", "DurableRelease", "DeliverySuppressed" },
            uncertain.Diagnostics.Failures.Select(item => item.Kind.ToString()));
        Assert.Null(await uncertain.ReactAsync());
        Assert.Equal(0, delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_or_completed_join_arguments_do_not_poison_an_unrelated_live_transaction(bool completed)
    {
        using var keys = KeyPair.Generate();
        var store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        using var transaction = new PackProjectionTransaction();
        var durable = transaction.Durable(() => new CountedUnit());
        if (completed) transaction.Commit();
        if (completed)
            Assert.Throws<InvalidOperationException>(() => { _ = installer.InstallAsync(ReadOnlyMemory<byte>.Empty, Context(keys), transaction); });
        else
        {
            Assert.Throws<ArgumentNullException>(() => { _ = installer.InstallAsync(ReadOnlyMemory<byte>.Empty, null!, transaction); });
            transaction.Commit();
        }
        Assert.Equal(1, durable.Commits);
        transaction.Dispose();
        Assert.Null(await transaction.ReactAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_postflush_tracking_fault_and_second_save_sql_fault_poison_whole_joined_unit(bool secondSave)
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var audit = new KernelAuditPackInstallAudit(origin.Trail, node, NullLogger<KernelAuditPackInstallAudit>.Instance, origin.Outbox);
        var factory = new FaultFactory(origin.Store.PacksFactory);
        var store = new DurablePackInstallStore(factory, audit);
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.AllowGate());
        PlatformPackTestPreload.Activate(store, Tenant);
        var original = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(original, Context(keys))).Installed);
        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.t1098-form", new JsonObject { ["title"] = "before" }));
        var next = await NextVersionAsync(original, keys);
        var before = await DurableSnapshotAsync(origin);
        if (secondSave)
            await origin.ExecuteAsync("""CREATE TRIGGER t1098_fail_audit BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't1098 second SaveChanges'); END;""");
        using var transaction = new PackProjectionTransaction();
        var sibling = "before";
        var cleaned = 0;
        var delivered = 0;
        transaction.BeginJoinedInstall();
        transaction.Stage(new object(), () => { sibling = "staged"; return () => sibling = "before"; });
        transaction.Finally(() => cleaned++);
        transaction.AfterCommit(() => { delivered++; return Task.CompletedTask; });
        transaction.Enlist(store);
        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.sibling", new JsonObject { ["witness"] = "staged-sibling" }));
        factory.SecondSave = secondSave;
        factory.Armed = true;
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(next, Context(keys), transaction));
        factory.Armed = false;
        Assert.Equal(1, factory.CompletedSaves);
        Assert.Equal((1L, (string?)"2.0.0", 0L), factory.FirstFlush);
        if (secondSave)
        {
            Assert.IsType<DbUpdateException>(thrown);
            Assert.Same(factory.SaveFailure, thrown);
            Assert.Contains("t1098 second SaveChanges", thrown.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, factory.SavesStarted);
            Assert.True(factory.OutboxTracked);
        }
        else
        {
            Assert.Same(factory.TrackingFailure, thrown);
            Assert.Equal(1, factory.SavesStarted);
        }
        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(transaction.Commit).InnerException);
        Assert.Equal(("staged", 0, 0), (sibling, cleaned, delivered));
        transaction.Dispose();
        Assert.Null(await transaction.ReactAsync());
        Assert.Equal(("before", 1, 0), (sibling, cleaned, delivered));
        Assert.Equal(before, await DurableSnapshotAsync(origin));
        if (secondSave) await origin.ExecuteAsync("DROP TRIGGER t1098_fail_audit;");
        Assert.True((await installer.InstallAsync(next, Context(keys))).Installed);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Independent_store_contenders_select_actual_equal_conflict_or_absent_after_rollback(
        bool reverseWinner, bool different, bool rollbackWinner)
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        await using var firstHandle = SearchTestStore.Reopen(origin.Store);
        await using var secondHandle = SearchTestStore.Reopen(origin.Store);
        var audit = new KernelAuditPackInstallAudit(origin.Trail, node, NullLogger<KernelAuditPackInstallAudit>.Instance, origin.Outbox);
        var first = new DurablePackInstallStore(new FiniteFactory(firstHandle.PacksFactory), audit);
        var second = new DurablePackInstallStore(new FiniteFactory(secondHandle.PacksFactory), audit);
        PlatformPackTestPreload.Activate(first, Tenant);
        var artifactA = await ExportAsync(keys);
        var codec = new PackFileCodec();
        var file = codec.TryDecode(artifactA)!;
        var changed = file.Envelope!.Payload with { Manifest = file.Envelope.Payload.Manifest with { Description = "independent B" } };
        var artifactB = different ? codec.Encode(file with
        { Envelope = await new Ed25519Signer(keys).SignAsync(changed, Now, Guid.NewGuid()) }) : artifactA;
        var firstPlan = await PrepareAsync(first, artifactA, keys, audit);
        var secondPlan = await PrepareAsync(second, artifactB, keys, audit);
        var winner = reverseWinner ? second : first;
        var contender = reverseWinner ? first : second;
        var winnerPlan = reverseWinner ? secondPlan : firstPlan;
        var contenderPlan = reverseWinner ? firstPlan : secondPlan;
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transaction = new PackProjectionTransaction();
        transaction.BeginJoinedInstall();
        transaction.Enlist(winner);
        var selected = winner.Commit(winnerPlan);
        Assert.Equal("Written", selected.Disposition.ToString());
        Assert.Same(winnerPlan.Audit, selected.SelectedAudit);
        transaction.AfterCommit(() => AppendSelectedAsync(selected, audit));
        string? committedWinnerBusiness = null;
        Task<PackInstallCommitResult> pending;
        using (ExecutionContext.SuppressFlow())
            pending = Task.Run(async () =>
            {
                attempted.TrySetResult();
                var result = contender.Commit(contenderPlan);
                await AppendSelectedAsync(result, audit);
                return result;
            });
        try
        {
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!rollbackWinner)
            {
                transaction.Commit();
                committedWinnerBusiness = await DurableSnapshotAsync(origin, includeAudit: false);
            }
        }
        finally { transaction.Dispose(); }
        Assert.Null(await transaction.ReactAsync());
        var actual = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(rollbackWinner ? "Written" : different ? "Conflict" : "Unchanged", actual.Disposition.ToString());
        if (!rollbackWinner && different) Assert.Null(actual.SelectedAudit);
        else Assert.Same(rollbackWinner ? contenderPlan.Audit : contenderPlan.UnchangedAudit, actual.SelectedAudit);
        await using var reopened = origin.Reopen();
        var read = new DurablePackInstallStore(reopened.Store.PacksFactory);
        Assert.Equal("1.0.0", read.GetVersion(Tenant, PackKey, "1.0.0")!.Version);
        Assert.Equal(rollbackWinner ? contenderPlan.InstalledPack.ArtifactIdentity : winnerPlan.InstalledPack.ArtifactIdentity,
            read.GetVersion(Tenant, PackKey, "1.0.0")!.ArtifactIdentity);
        Assert.Single(read.ListInstalled(Tenant), item => item.PackKey == PackKey);
        if (!rollbackWinner)
            Assert.Equal(committedWinnerBusiness, await DurableSnapshotAsync(origin, includeAudit: false));
        Assert.Equal(rollbackWinner || different ? new[] { "Installed" } : new[] { "Installed", "Unchanged" }, await OutboxActionsAsync(reopened));
        output.WriteLine("Contender start proves attempted overlap only; it does not measure native SQLite waiting.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_override_and_activation_survive_identical_retry_in_both_orders(bool retryFirst)
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var (first, installer) = Compose(origin, node);
        PlatformPackTestPreload.Activate(first, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        await using var independent = SearchTestStore.Reopen(origin.Store);
        var second = new DurablePackInstallStore(independent.PacksFactory);
        var audit = new KernelAuditPackInstallAudit(origin.Trail, node, NullLogger<KernelAuditPackInstallAudit>.Instance, origin.Outbox);
        first = new DurablePackInstallStore(origin.Store.PacksFactory, audit);
        var retryPlan = await PrepareAsync(first, bytes, keys, audit);
        var installedAt = first.GetVersion(Tenant, PackKey, Version)!.InstalledAtUtc;
        var beforeRetryOrEdit = await DurableSnapshotAsync(origin, includeAudit: false);
        string afterLegitimateEdit;
        PackInstallCommitResult retry;
        if (retryFirst)
        {
            retry = first.Commit(retryPlan);
            // Pin every raw business row before the legitimate later activation/overlay may change it.
            Assert.Equal(beforeRetryOrEdit, await DurableSnapshotAsync(origin, includeAudit: false));
            second.Activate(Tenant, PackKey, Version);
            second.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.t1098-form", JsonNode.Parse("""{"witness":"concurrent-override"}""")!));
            afterLegitimateEdit = await DurableSnapshotAsync(origin, includeAudit: false);
        }
        else
        {
            second.Activate(Tenant, PackKey, Version);
            second.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.t1098-form", JsonNode.Parse("""{"witness":"concurrent-override"}""")!));
            afterLegitimateEdit = await DurableSnapshotAsync(origin, includeAudit: false);
            retry = first.Commit(retryPlan);
        }
        await AppendSelectedAsync(retry, audit);
        Assert.Equal("Unchanged", retry.Disposition.ToString());
        await using var restarted = origin.Reopen();
        var read = new DurablePackInstallStore(restarted.Store.PacksFactory);
        Assert.Equal("Active", read.GetVersion(Tenant, PackKey, Version)!.Lifecycle.ToString());
        Assert.Equal("1.0.0", read.GetActive(Tenant, PackKey)!.Version);
        Assert.Equal("""{"witness":"concurrent-override"}""", Assert.Single(read.GetOverrides(Tenant, PackKey)).OverlayPatch.ToJsonString());
        Assert.Equal(installedAt, read.GetVersion(Tenant, PackKey, Version)!.InstalledAtUtc);
        Assert.Equal(afterLegitimateEdit, await DurableSnapshotAsync(origin, includeAudit: false));
        Assert.Equal(new[] { "Installed", "Unchanged" }, await OutboxActionsAsync(restarted));
    }

    [Fact]
    public async Task Absent_new_version_retains_stale_override_readset_refusal()
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var (first, installer) = Compose(origin, node);
        PlatformPackTestPreload.Activate(first, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        var audit = new KernelAuditPackInstallAudit(origin.Trail, node, NullLogger<KernelAuditPackInstallAudit>.Instance, origin.Outbox);
        var plan = await PrepareAsync(first, await NextVersionAsync(bytes, keys), keys, audit);
        await using var independent = SearchTestStore.Reopen(origin.Store);
        var second = new DurablePackInstallStore(independent.PacksFactory);
        second.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.t1098-form", JsonNode.Parse("""{"witness":"concurrent-override"}""")!));
        var before = await DurableSnapshotAsync(origin);
        Assert.Throws<PackInstallStateChangedException>(() => first.Commit(plan));
        Assert.Equal(before, await DurableSnapshotAsync(origin));
        Assert.Null(first.GetVersion(Tenant, PackKey, "2.0.0"));
    }

    [Fact]
    public async Task Actual_held_immediate_sqlite_lock_causes_finite_provider_failure_without_guessed_success()
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var audit = new KernelAuditPackInstallAudit(origin.Trail, node, NullLogger<KernelAuditPackInstallAudit>.Instance, origin.Outbox);
        var store = new DurablePackInstallStore(new FiniteFactory(origin.Store.PacksFactory), audit);
        PlatformPackTestPreload.Activate(store, Tenant);
        var plan = await PrepareAsync(store, await ExportAsync(keys), keys, audit);
        var before = await DurableSnapshotAsync(origin);
        var held = new PackProjectionSqliteUnit(origin.Store.CreatePacksContext());
        try
        {
            var attempted = Task.Run(() => store.Commit(plan));
            var failure = await Assert.ThrowsAsync<SqliteException>(async () => await attempted.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains(failure.SqliteErrorCode, new[] { 5, 6 });
            output.WriteLine("Actual held-lock provider failure: " + failure.SqliteErrorCode);
        }
        finally { held.Dispose(); }
        Assert.Equal(before, await DurableSnapshotAsync(origin));
        Assert.Null(store.GetVersion(Tenant, PackKey, Version));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    public async Task Durable_legacy_identity_refuses_without_rewriting_raw_payload(string kind)
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var (store, installer) = Compose(origin, node);
        PlatformPackTestPreload.Activate(store, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        string payload;
        await using (var db = origin.Store.CreatePacksContext())
        {
            var row = await db.InstalledVersions.SingleAsync(item => item.Tenant == Tenant.Value && item.PackKey == PackKey);
            var json = JsonNode.Parse(row.PayloadJson)!.AsObject();
            if (kind == "missing") Assert.True(json.Remove("ArtifactIdentity"));
            else
            {
                var identity = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(json["ArtifactIdentity"]!.GetValue<string>())))!;
                identity["version"] = 2;
                json["ArtifactIdentity"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.ToJsonString()));
            }
            row.PayloadJson = payload = json.ToJsonString();
            await db.SaveChangesAsync();
        }
        var before = await DurableSnapshotAsync(origin, includeAudit: false);
        Assert.Equal(new[] { "pack.install.refused.same_version_identity_unknown" }, installer.Check(bytes, Context(keys)).RefusalCodes);
        var refused = await installer.InstallAsync(bytes, Context(keys));
        Assert.False(refused.Installed);
        Assert.Equal(new[] { "pack.install.refused.same_version_identity_unknown" }, refused.RefusalCodes);
        Assert.Equal(before, await DurableSnapshotAsync(origin, includeAudit: false));
        await using var reopened = origin.Reopen();
        await using var read = reopened.Store.CreatePacksContext();
        Assert.Equal(payload, (await read.InstalledVersions.SingleAsync(item => item.Tenant == Tenant.Value && item.PackKey == PackKey)).PayloadJson);
    }

    [Fact]
    public async Task Unrelated_factory_operational_faults_propagate_exactly_and_never_become_Unknown()
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var (store, installer) = Compose(origin, node);
        PlatformPackTestPreload.Activate(store, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        var before = await DurableSnapshotAsync(origin);
        var failure = new IOException("unrelated database factory failure");
        var failedStore = new DurablePackInstallStore(new FailingFactory(failure));
        var failed = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), failedStore,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        Assert.Same(failure, Assert.Throws<IOException>(() => failed.Preview(bytes, Context(keys))));
        Assert.Same(failure, Assert.Throws<IOException>(() => failed.Check(bytes, Context(keys))));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failed.InstallAsync(bytes, Context(keys))));
        Assert.Equal(before, await DurableSnapshotAsync(origin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Joined_Unchanged_selects_audit_only_after_release_and_its_sql_failure_discards_siblings(bool failAudit)
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var audit = new KernelAuditPackInstallAudit(origin.Trail, node, NullLogger<KernelAuditPackInstallAudit>.Instance, origin.Outbox);
        var store = new DurablePackInstallStore(origin.Store.PacksFactory, audit);
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.AllowGate());
        PlatformPackTestPreload.Activate(store, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        var before = await DurableSnapshotAsync(origin);
        var installedBefore = JsonSerializer.Serialize(store.ListInstalled(Tenant));
        var deliveredBefore = audit.Query(Tenant).Count;
        if (failAudit)
            await origin.ExecuteAsync("""CREATE TRIGGER t1098_noop_fail BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'Unchanged audit SQL failure'); END;""");
        using var transaction = new PackProjectionTransaction();
        transaction.BeginJoinedInstall();
        transaction.Enlist(store);
        store.SaveOverride(Tenant, PackKey, new PackTenantOverride("fixtures.sibling", new JsonObject { ["witness"] = "prior staged sibling" }));
        if (failAudit)
        {
            var original = await Assert.ThrowsAsync<DbUpdateException>(() => installer.InstallAsync(bytes, Context(keys), transaction));
            Assert.Contains("Unchanged audit SQL failure", original.ToString(), StringComparison.Ordinal);
            Assert.Same(original, Assert.Throws<InvalidOperationException>(transaction.Commit).InnerException);
        }
        else
        {
            var accepted = await installer.InstallAsync(bytes, Context(keys), transaction);
            Assert.Equal("Unchanged", accepted.Action.ToString());
            Assert.Equal(deliveredBefore, audit.Query(Tenant).Count);
            transaction.Commit();
        }
        transaction.Dispose();
        Assert.Equal(deliveredBefore, audit.Query(Tenant).Count);
        Assert.Null(await transaction.ReactAsync());
        if (failAudit)
        {
            Assert.Equal(before, await DurableSnapshotAsync(origin));
            Assert.Equal(deliveredBefore, audit.Query(Tenant).Count);
        }
        else
        {
            Assert.Equal(installedBefore, JsonSerializer.Serialize(store.ListInstalled(Tenant)));
            Assert.Equal(deliveredBefore + 1, audit.Query(Tenant).Count);
            Assert.Equal("Unchanged", audit.Query(Tenant).Last().Action.ToString());
            Assert.Equal("""{"witness":"prior staged sibling"}""", Assert.Single(store.GetOverrides(Tenant, PackKey)).OverlayPatch.ToJsonString());
        }
    }

    private sealed class FailingFactory(IOException failure) : IDbContextFactory<NodeLocalPacksDbContext>
    {
        public NodeLocalPacksDbContext CreateDbContext() => throw failure;
    }

    [Fact]
    public async Task Durable_identity_binds_actual_row_coordinates_and_never_repairs_mismatched_payload()
    {
        using var keys = KeyPair.Generate();
        using var node = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await using var origin = await DurableAuditHarness.CreateAsync();
        await using (var db = origin.Store.CreatePacksContext()) await db.Database.MigrateAsync();
        var (store, installer) = Compose(origin, node);
        PlatformPackTestPreload.Activate(store, Tenant);
        var bytes = await ExportAsync(keys);
        Assert.True((await installer.InstallAsync(bytes, Context(keys))).Installed);
        string payload;
        await using (var db = origin.Store.CreatePacksContext())
        {
            var row = await db.InstalledVersions.SingleAsync(item => item.Tenant == Tenant.Value && item.PackKey == PackKey);
            var pack = JsonSerializer.Deserialize<InstalledPack>(row.PayloadJson)!;
            var frame = Encoding.UTF8.GetString(Convert.FromBase64String(pack.ArtifactIdentity!))
                .Replace("test.t1098-a3", "test.other", StringComparison.Ordinal);
            row.PayloadJson = payload = JsonSerializer.Serialize(pack with
            { PackKey = "test.other", ArtifactIdentity = Convert.ToBase64String(Encoding.UTF8.GetBytes(frame)) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(new[] { "pack.install.refused.same_version_identity_unknown" }, installer.Preview(bytes, Context(keys)).RefusalCodes);
        var refused = await installer.InstallAsync(bytes, Context(keys));
        Assert.False(refused.Installed);
        Assert.Equal(new[] { "pack.install.refused.same_version_identity_unknown" }, refused.RefusalCodes);
        await using var reopened = origin.Reopen();
        await using var read = reopened.Store.CreatePacksContext();
        Assert.Equal(payload, (await read.InstalledVersions.SingleAsync(item => item.Tenant == Tenant.Value && item.PackKey == PackKey)).PayloadJson);
    }

    private static async Task<PackInstallTransaction> PrepareAsync(DurablePackInstallStore store, byte[] bytes,
        KeyPair keys, KernelAuditPackInstallAudit audit)
    {
        var codec = new PackFileCodec();
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        Assert.Equal("Verified", verifier.Verify(bytes, Context(keys).TrustStore).Verdict.ToString());
        var memory = new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate(memory, Tenant);
        var engine = new PackInstaller(verifier, memory, new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var accepted = await engine.InstallAsync(bytes, Context(keys));
        Assert.True(accepted.Installed, string.Join(",", accepted.RefusalCodes));
        var pack = memory.GetVersion(Tenant, accepted.PackKey, accepted.Version)!;
        var decision = accepted.Decision!;
        var written = new PackCommitAudit([new PackInstallAuditEntry(Tenant, PackInstallAuditAction.Installed,
            PackKey, pack.Version, Now, keys.PrincipalId, 1, null, ActingPrincipal: Operator)], decision);
        var unchanged = new PackCommitAudit([new PackInstallAuditEntry(Tenant, PackInstallAuditAction.Unchanged,
            PackKey, pack.Version, Now, keys.PrincipalId, 1, null, ActingPrincipal: Operator)], decision);
        foreach (var entry in written.Entries.Concat(unchanged.Entries)) await audit.PrepareAuthorizedAsync(entry, decision);
        return new PackInstallTransaction(Tenant, pack, memory.GetWatermark(Tenant, PackKey)!, [])
        {
            Audit = written, UnchangedAudit = unchanged, CompareWatermark = true,
            ExpectedWatermark = store.GetWatermark(Tenant, PackKey), ExpectedInstalledState = store.ListInstalled(Tenant),
            ExpectedOverrides = store.GetOverrides(Tenant, PackKey), ExpectedKeyOwnership = store.GetKeyOwnership(Tenant),
        };
    }

    private static async Task AppendSelectedAsync(PackInstallCommitResult selected, KernelAuditPackInstallAudit audit)
    {
        if (selected.SelectedAudit is not { } bundle) return;
        foreach (var entry in bundle.Entries) await audit.AppendAuthorizedAsync(entry, bundle.Decision);
    }

    private static async Task<string[]> OutboxActionsAsync(DurableAuditHarness harness)
    {
        await using var db = harness.Store.CreatePacksContext();
        var bodies = await db.Set<AuditOutboxRow>().AsNoTracking().Where(row => row.TargetId == PackKey)
            .Select(row => row.BodyJson).ToArrayAsync();
        return bodies.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("action").GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class FiniteFactory(IDbContextFactory<NodeLocalPacksDbContext> inner) : IDbContextFactory<NodeLocalPacksDbContext>
    {
        public NodeLocalPacksDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            ((SqliteConnection)db.Database.GetDbConnection()).DefaultTimeout = 1;
            db.Database.SetCommandTimeout(1);
            return db;
        }
    }

    private static async Task<byte[]> NextVersionAsync(byte[] bytes, KeyPair keys, string version = "2.0.0")
    {
        var codec = new PackFileCodec();
        var file = codec.TryDecode(bytes)!;
        var subject = file.Envelope!.Payload with { Manifest = file.Envelope.Payload.Manifest with { Version = version } };
        var signed = await new Ed25519Signer(keys).SignAsync(subject, Now, Guid.NewGuid());
        return codec.Encode(file with { Envelope = signed });
    }

    private static async Task<string> DurableSnapshotAsync(DurableAuditHarness origin, bool includeAudit = true)
    {
        await using var reopened = origin.Reopen();
        await using var db = reopened.Store.CreatePacksContext();
        return string.Join("\n", Rows(db.InstalledVersions), Rows(db.Watermarks), Rows(db.Overrides),
            Rows(db.KeyOwnership), Rows(db.ProjectionAdmissions), includeAudit ? Rows(db.Set<AuditOutboxRow>()) : string.Empty);
        static string Rows<T>(IQueryable<T> rows) where T : class => string.Join("\n",
            rows.AsNoTracking().AsEnumerable().Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal));
    }

    private sealed class FaultFactory(IDbContextFactory<NodeLocalPacksDbContext> inner) : IDbContextFactory<NodeLocalPacksDbContext>
    {
        internal bool Armed { get; set; }
        internal bool SecondSave { get; set; }
        internal IOException TrackingFailure { get; } = new("actual StageRecord Add after SQL flush");
        internal Exception? SaveFailure { get; private set; }
        internal int SavesStarted { get; private set; }
        internal int CompletedSaves { get; private set; }
        internal bool OutboxTracked { get; private set; }
        internal (long VersionRows, string? Watermark, long Overrides) FirstFlush { get; private set; }
        public NodeLocalPacksDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            var writer = false;
            db.SavingChanges += (_, _) =>
            {
                if (!Armed) return;
                writer |= db.ChangeTracker.Entries<PackInstalledVersionRow>().Any(entry =>
                    entry.State == EntityState.Added && entry.Entity.PackKey == PackKey && entry.Entity.Version == "2.0.0");
                if (writer) SavesStarted++;
            };
            db.SavedChanges += (_, _) =>
            {
                if (!Armed || !writer) return;
                CompletedSaves++;
                if (CompletedSaves != 1) return;
                FirstFlush = (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM pack_installed_versions WHERE pack_key = 'test.t1098-a3' AND version = '2.0.0'")),
                    Scalar("SELECT version FROM pack_watermarks WHERE pack_key = 'test.t1098-a3'")?.ToString(),
                    Convert.ToInt64(Scalar("SELECT COUNT(*) FROM pack_overrides WHERE pack_key = 'test.t1098-a3'")));
            };
            db.SaveChangesFailed += (_, args) => { if (Armed && writer) SaveFailure = args.Exception; };
            db.ChangeTracker.Tracked += (_, args) =>
            {
                if (!Armed || !writer || args.FromQuery || args.Entry.Entity is not AuditOutboxRow || args.Entry.State != EntityState.Added) return;
                OutboxTracked = true;
                if (!SecondSave && CompletedSaves == 1) throw TrackingFailure;
            };
            return db;

            object? Scalar(string sql)
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
    }

    private sealed class CountedUnit : IPackProjectionDurableUnit
    {
        internal int Commits { get; private set; }
        internal int Disposals { get; private set; }
        internal Exception? CommitFailure { get; init; }
        internal Exception? DisposeFailure { get; init; }
        public void Commit() { Commits++; if (CommitFailure is not null) throw CommitFailure; }
        public void Dispose() { Disposals++; if (DisposeFailure is not null) throw DisposeFailure; }
    }

    private sealed class UnsupportedJoinedInstaller(IPackInstaller inner) : IPackInstaller
    {
        public PackInstallPreview Preview(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Preview(bytes, context);
        public PackInstallPreview Check(ReadOnlySpan<byte> bytes, PackInstallContext context) => inner.Check(bytes, context);
        public Task<PackInstallOutcome> InstallAsync(ReadOnlyMemory<byte> bytes, PackInstallContext context, CancellationToken cancellationToken = default) => inner.InstallAsync(bytes, context, cancellationToken);
        public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string key, string version, CancellationToken cancellationToken = default) => inner.ActivateAsync(context, key, version, cancellationToken);
        public Task<PackDeactivationOutcome> DeactivateAsync(PackInstallContext context, string key, string version, CancellationToken cancellationToken = default) => inner.DeactivateAsync(context, key, version, cancellationToken);
        public Task<PackNarrowingOutcome> NarrowAsync(PackInstallContext context, string key, string content, JsonNode patch, AuthorizationDecision decision, CancellationToken cancellationToken = default) => inner.NarrowAsync(context, key, content, patch, decision, cancellationToken);
    }

    private static (DurablePackInstallStore Store, PackInstaller Installer) Compose(
        DurableAuditHarness harness, NodePrincipalSigner nodeSigner)
    {
        var audit = new KernelAuditPackInstallAudit(harness.Trail, nodeSigner,
            NullLogger<KernelAuditPackInstallAudit>.Instance, harness.Outbox);
        var store = new DurablePackInstallStore(harness.Store.PacksFactory, audit);
        var verifier = new PackVerifier(new Ed25519Verifier(), new PackFileCodec());
        return (store, new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(), audit,
            TestAuthorization.AllowGate()));
    }

    private static PackInstallContext Context(KeyPair keys) => new(
        Tenant,
        new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, keys.PrincipalId, 1, TrustRootStatus.Current)]),
        new PackRevocationList(Array.Empty<PackRevokedKey>(), Now),
        AdmittedInstant.FromRecordedAct(Now), TimeSpan.FromDays(30), Principal: Operator);

    private static async Task<byte[]> ExportAsync(KeyPair keys)
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            new PackFileCodec(), new FixedClock());
        var exported = await exporter.ExportAsync(new PackExportRequest(
            PackKey, Version, "T-1098 lifecycle retry", "Identical active-version retry",
            PackScopeTier.Horizontal,
            [new PackContentSource("fixtures.t1098-form", PackContentKind.FormDefinition, "1.0.0",
                new JsonObject { ["title"] = "Lifecycle retry" })],
            Array.Empty<PackDependencyRef>(), Array.Empty<string>(), 1,
            Dcp: DomainComplianceProfile.General("t1098-a3-author")), new Ed25519Signer(keys));
        Assert.True(exported.Succeeded, string.Join(",", exported.Validation.Errors.Select(error => error.Code)));
        return Assert.IsType<byte[]>(exported.FileBytes);
    }

    private static async Task<Observation> ObserveAsync(DurableAuditHarness harness, DurablePackInstallStore store)
    {
        await using var db = harness.Store.CreatePacksContext();
        var row = await db.InstalledVersions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Tenant == Tenant.Value && item.PackKey == PackKey && item.Version == Version);
        var watermark = await db.Watermarks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Tenant == Tenant.Value && item.PackKey == PackKey);
        var overrides = await db.Overrides.AsNoTracking()
            .Where(item => item.Tenant == Tenant.Value && item.PackKey == PackKey)
            .OrderBy(item => item.ContentKey)
            .Select(item => new { item.ContentKey, item.OverlayJson }).ToArrayAsync();
        var version = store.GetVersion(Tenant, PackKey, Version);
        var active = store.GetActive(Tenant, PackKey);
        return new Observation(row?.Lifecycle, row?.PayloadJson, version?.Version, version?.Lifecycle.ToString(),
            active?.Version, active?.Lifecycle.ToString(), watermark?.Version, watermark?.FloorsJson,
            JsonSerializer.Serialize(store.GetWatermark(Tenant, PackKey)), JsonSerializer.Serialize(overrides),
            JsonSerializer.Serialize(store.GetOverrides(Tenant, PackKey)
                .Select(item => new { item.ContentKey, OverlayJson = item.OverlayPatch.ToJsonString() })));
    }

    private void WriteObservation(string phase, Observation observation) =>
        output.WriteLine(phase + ": " + JsonSerializer.Serialize(observation));

    private sealed record Observation(
        int? LifecycleColumn, string? PayloadJson, string? StoredVersion, string? VersionLifecycle,
        string? ActiveVersion, string? ActiveLifecycle, string? WatermarkVersion, string? FloorsJson,
        string ApiWatermarkJson, string DurableOverridesJson, string ApiOverridesJson);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
