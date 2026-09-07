using System.Diagnostics;
using System.Security.Cryptography;

using Xunit.Abstractions;

using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class TenantMembershipAuthorityStoreTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private const string AuthorityKey = "identity/tenant-membership-authority/v3";
    private const string ActorAccountId = "actor-1";
    private const string AuthorityEvidenceDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 13, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Prepare_Fences_Admission_Without_Exposing_Membership_Then_Finalizes_Atomically()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var mutation = Mutation(tenantId);

        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            mutation, FixedNow,
            CancellationToken.None);

        Assert.Null(await authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.True(await authority.IsAdmissionBlockedAsync("account-1", CancellationToken.None));
        Assert.Equal(TenantMembershipIntentState.Prepared,
            await authority.GetIntentStateAsync("command-1", CancellationToken.None));

        var receipt = await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);
        var membership = await authority.GetMembershipAsync("account-1", CancellationToken.None);

        Assert.NotNull(membership);
        Assert.Equal(tenantId, membership.TenantId);
        Assert.Equal(1, membership.OwnerVersion);
        Assert.False(await authority.IsAdmissionBlockedAsync("account-1", CancellationToken.None));
        Assert.Equal(TenantMembershipIntentState.Finalized,
            await authority.GetIntentStateAsync("command-1", CancellationToken.None));
        Assert.Equal(1, receipt.AuditSequence);
        Assert.Equal(1, receipt.MembershipOwnerVersion);
        Assert.NotEqual(receipt.MembershipDigest, receipt.IntentDigest);
    }

    [Fact]
    public async Task Finalization_Receipt_Remains_Exact_After_Later_Authority_Writes_And_Restart()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var first = Mutation(tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            first, FixedNow,
            CancellationToken.None);
        var original = await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        await authority.PrepareAsync(
            "command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId, "principal-2"),
            FixedNow.AddSeconds(2), CancellationToken.None);
        await authority.FinalizeAsync(
            "command-2", "fingerprint-2", FixedNow.AddSeconds(3), CancellationToken.None);
        await database.ReopenAsync();

        var reopened = Authority(database.Store, tenantId);
        var replay = await reopened.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddDays(1), CancellationToken.None);
        Assert.Equal(original, replay);
        Assert.Equal(1, replay.AuditSequence);
        Assert.Equal(2,
            (await reopened.FinalizeAsync(
                "command-2", "fingerprint-2", FixedNow.AddDays(1), CancellationToken.None)).AuditSequence);
    }

    [Fact]
    public async Task Abort_Leaves_No_Membership_And_Cannot_Be_Finalized()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);

        await authority.AbortAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        Assert.Null(await authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.False(await authority.IsAdmissionBlockedAsync("account-1", CancellationToken.None));
        Assert.Equal(TenantMembershipIntentState.Aborted,
            await authority.GetIntentStateAsync("command-1", CancellationToken.None));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(2), CancellationToken.None));
        Assert.StartsWith("identity.membership_intent_aborted:", exception.Message);
    }

    [Fact]
    public async Task Tenant_Binding_And_Changed_Replay_Are_Refused()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(Guid.NewGuid().ToString("D")), FixedNow,
            CancellationToken.None));
        Assert.StartsWith("identity.tenant_binding_mismatch:", mismatch.Message);

        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => authority.PrepareAsync(
            "command-1", "fingerprint-2", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None));
        Assert.StartsWith("identity.membership_changed_replay:", changed.Message);
    }

    [Fact]
    public async Task Two_Independent_Adapters_Do_Not_Lose_Concurrent_Authority_Writes()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        await using var secondStore = await database.OpenAdditionalAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var first = Authority(database.Store, tenantId);
        var second = Authority(secondStore, tenantId);

        await Task.WhenAll(
            first.PrepareAsync(
                "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
                Mutation(tenantId, "principal-1"), FixedNow,
                CancellationToken.None),
            second.PrepareAsync(
                "command-2", "fingerprint-2", "account-2", ActorAccountId, AuthorityEvidenceDigest,
                Mutation(tenantId, "principal-2"), FixedNow,
                CancellationToken.None));
        await Task.WhenAll(
            first.FinalizeAsync("command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None),
            second.FinalizeAsync("command-2", "fingerprint-2", FixedNow.AddSeconds(1), CancellationToken.None));

        Assert.NotNull(await first.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.NotNull(await first.GetMembershipAsync("account-2", CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_Authority_Evidence_Is_Refused()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        var bytes = await database.Store.GetAsync(AuthorityKey, CancellationToken.None);
        Assert.NotNull(bytes);
        var json = System.Text.Encoding.UTF8.GetString(bytes!);
        var tampered = System.Text.Encoding.UTF8.GetBytes(
            json.Replace("TenantMembershipChanged", "TenantMembershipGranted", StringComparison.Ordinal));
        await database.Store.SetAsync(AuthorityKey, tampered, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    [Fact]
    public async Task Membership_Row_Cannot_Diverge_From_Its_Latest_Finalized_Intent()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        await authority.PrepareAsync(
            "command-1", "fingerprint-1", "account-1", ActorAccountId, AuthorityEvidenceDigest,
            Mutation(tenantId), FixedNow,
            CancellationToken.None);
        await authority.FinalizeAsync(
            "command-1", "fingerprint-1", FixedNow.AddSeconds(1), CancellationToken.None);

        var bytes = await database.Store.GetAsync(AuthorityKey, CancellationToken.None);
        var json = System.Text.Encoding.UTF8.GetString(bytes!);
        const string original = "\"canonicalPrincipalId\":\"principal-1\"";
        const string replacement = "\"canonicalPrincipalId\":\"invented-01\"";
        var index = json.IndexOf(original, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var tampered = string.Concat(
            json.AsSpan(0, index),
            replacement,
            json.AsSpan(index + original.Length));
        await database.Store.SetAsync(
            AuthorityKey,
            System.Text.Encoding.UTF8.GetBytes(tampered),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.GetMembershipAsync("account-1", CancellationToken.None));
        Assert.StartsWith("identity.tenant_authority_invalid:", exception.Message);
    }

    [Fact]
    public async Task Representative_Growth_Remains_Bounded_Well_Below_The_Four_Mib_Ceiling()
    {
        await using var database = await TenantStoreDatabase.CreateAsync();
        var tenantId = Guid.NewGuid().ToString("D");
        var authority = Authority(database.Store, tenantId);
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
        {
            var correlation = $"growth-{index:D3}";
            var fingerprint = $"fingerprint-{index:D3}";
            var account = $"account-{index:D3}";
            await authority.PrepareAsync(
                correlation,
                fingerprint,
                account,
                ActorAccountId,
                AuthorityEvidenceDigest,
                Mutation(tenantId, $"principal-{index:D3}"),
                FixedNow.AddSeconds(index * 2),
                CancellationToken.None);
            await authority.FinalizeAsync(
                correlation,
                fingerprint,
                FixedNow.AddSeconds((index * 2) + 1),
                CancellationToken.None);
        }
        timer.Stop();

        var bytes = await database.Store.GetAsync(AuthorityKey, CancellationToken.None);
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length < 1024 * 1024, $"100-member authority used {bytes.Length} bytes.");

        // The WALL-CLOCK assertion that stood here (`timer.Elapsed < 30s`, from earlier repository ticket #2662) is
        // removed, and its stopwatch reading is reported instead of asserted.
        //
        // It did not test what this test is named for. The invariant here is the SIZE bound above;
        // elapsed time measures how busy the machine was. This assembly runs at xunit's default
        // parallelism inside a whole-solution run, so the reading is a function of load: 4s in
        // isolation, 4s with this assembly alone, 32s -- and red -- under `dotnet test Harborline.Api.slnx`
        // once ComposedHostBootSmokeTests began spawning three host processes. It was the only
        // wall-clock assertion in 1,679 tests, and any future test that adds load would have tripped
        // it the same way. A gate whose colour is decided by something other than the property it
        // names is not a gate.
        //
        // A genuine guard against pathological (e.g. O(n^2)) growth belongs in a benchmark with a
        // quiet machine and a baseline, not in a parallel unit suite. The size bound above already
        // catches the storage-shape regression this fixture exists to catch.
        // ITestOutputHelper, NOT Console.WriteLine: xunit v2 (pinned at 2.9.3 in Directory.Packages.props)
        // does not capture Console, so a Console.WriteLine here would be unattributed interleaved stdout
        // under parallelism -- the diagnostic this replacement exists to preserve would not survive.
        _output.WriteLine(
            $"100-member growth fixture: {bytes.Length} bytes in {timer.Elapsed.TotalSeconds:F1}s " +
            "(timing is reported, not asserted -- it is load-dependent).");
    }

    private static EncryptedTenantMembershipAuthorityStore Authority(IEncryptedStore store, string tenantId) =>
        new(store, tenantId, new TestHomeDecisionAuthority(), new FixedTimeProvider(FixedNow));

    private static TenantMembershipMutation Mutation(string tenantId, string principalId = "principal-1") =>
        new(
            tenantId,
            principalId,
            $"grant-{principalId}",
            ExpectedGrantOwnerVersion: 1,
            AuthorizationEpoch: 1,
            ExpectedMembershipOwnerVersion: 0,
            TenantMembershipStatus.Active);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestHomeDecisionAuthority : IInstallationIdentityHomeDecisionAuthority
    {
        public Task<InstallationIdentityHomeDecisionReceipt> RequireFinalizationAsync(
            string correlationId,
            string commandFingerprint,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Receipt(
                correlationId, commandFingerprint, tenantId, InstallationIdentityCoordinatorState.Committing));

        public Task<InstallationIdentityHomeDecisionReceipt> RequireAbortAsync(
            string correlationId,
            string commandFingerprint,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Receipt(
                correlationId, commandFingerprint, tenantId, InstallationIdentityCoordinatorState.Aborted));

        private static InstallationIdentityHomeDecisionReceipt Receipt(
            string correlationId,
            string commandFingerprint,
            string tenantId,
            InstallationIdentityCoordinatorState state) =>
            new(
                correlationId,
                commandFingerprint,
                state,
                1,
                tenantId,
                InstallationAuditIntegrity.Hash(correlationId, commandFingerprint, tenantId, state.ToString()));
    }

    private sealed class TenantStoreDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly byte[] _key;

        private TenantStoreDatabase(string path, byte[] key, SqlCipherEncryptedStore store)
        {
            _path = path;
            _key = key;
            Store = store;
        }

        public SqlCipherEncryptedStore Store { get; private set; }

        public static async Task<TenantStoreDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"harborline-membership-{Guid.NewGuid():N}.db");
            var key = RandomNumberGenerator.GetBytes(32);
            var store = new SqlCipherEncryptedStore();
            await store.OpenAsync(path, key, CancellationToken.None);
            return new TenantStoreDatabase(path, key, store);
        }

        public async Task<SqlCipherEncryptedStore> OpenAdditionalAsync()
        {
            var store = new SqlCipherEncryptedStore();
            await store.OpenAsync(_path, _key, CancellationToken.None);
            return store;
        }

        public async Task ReopenAsync()
        {
            await Store.CloseAsync();
            await Store.DisposeAsync();
            Store = await OpenAdditionalAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
