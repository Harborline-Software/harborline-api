using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Tests.Packs;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Kernel.Core;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-909 slice 6 (T-587): the offline recover-configuration verb's adapters over the real SQLCipher packs store,
/// with crash residue produced by the real activation target rather than hand-written rows.
/// </summary>
public sealed class ConfigurationRecoveryCommandTests : IAsyncLifetime
{
    private const string CmPack = "harborline.configuration-management";
    private static readonly DateTimeOffset Frozen = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Recovered = Frozen.AddHours(2);
    private static readonly TenantId Tenant = new("tenant-recovery");

    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationActivationTarget _target = null!;
    private Harborline.Api.Kernel.Audit.InMemoryAuditTrail _trail = null!;
    private ConfigurationEvidenceOutbox _drainer = null!;
    private ConfigurationEvidenceOutbox _verbEvidence = null!;
    private string _dataDirectory = null!;
    private FileStream _runLock = null!;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 58);
        _store = new DurablePackInstallStore(_db.Factory);
        // One trail; the running host's drain and the offline verb each hold their own outbox over it, as two processes would.
        _trail = new Harborline.Api.Kernel.Audit.InMemoryAuditTrail();
        var signer = new Ed25519Signer(KeyPair.Generate());
        _drainer = new ConfigurationEvidenceOutbox(_db.Factory, _trail, _trail, signer, new FixedTime(Frozen),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationEvidenceOutbox>.Instance);
        _verbEvidence = new ConfigurationEvidenceOutbox(_db.Factory, _trail, _trail, signer, new FixedTime(Recovered),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationEvidenceOutbox>.Instance);
        _target = new ConfigurationActivationTarget(_db.Factory, _store, TestPackGate.AllowAll(), _drainer);
        // A non-ASCII content key: the canonical writer and the stored JSON must escape it identically.
        Seed("acme.core", ["form.shared", "form.café"]);
        Seed("acme.ext", ["form.shared", "form.ext"]);
        Seed(CmPack, ["configuration.surface"]);
        _store.RecordKeyOwnership(Tenant, "form.shared", "acme.core");
        _dataDirectory = Directory.CreateTempSubdirectory("harborline-recover-configuration-").FullName;
        _runLock = NodeRunLock.TryAcquire(_dataDirectory, createDirectory: false)!;
    }

    public async Task DisposeAsync()
    {
        _drainer.Dispose();
        _verbEvidence.Dispose();
        await _runLock.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { /* best-effort */ }
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Digest_the_kernel_verifies_matches_the_host_effective_pointer()
    {
        await ActivateAsync("intent-digest", "acme.ext");
        using var context = _db.CreateContext();
        var pointer = context.EffectiveGenerations.AsNoTracking().Single();

        // The kernel checks sha256(content) == pointer.Digest. The host's Digest is the platform generation
        // digest over its canonical reference document, and ReferencesJson is that document re-serialized.
        Assert.Equal(pointer.Digest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pointer.ReferencesJson))));
        var snapshot = await new ConfigurationRecoveryCommand.ProfileReader(_db.Factory).ReadAsync(Tenant.Value);
        Assert.Equal(pointer.Digest, Convert.ToHexStringLower(SHA256.HashData(snapshot!.EffectiveContent!.Value.Span)));
    }

    [Fact]
    public async Task Recover_configuration_with_the_CM_pack_deactivated_confirms_pointer_abandons_prepared_publishes_outbox()
    {
        var (abandoned, effective) = await CrashResidueAsync();
        _store.Deactivate(Tenant, CmPack, "1.0.0");
        var activatedAt = Pointer().ActivatedAt;

        var result = await RecoverAsync("Node crashed before publishing intent-2");

        Assert.True(result.Committed, result.Refusal?.Code);
        Assert.Equal(effective, result.Record!.EffectiveDigest);
        Assert.Equal(
            [
                new ConfigurationRepair(ConfigurationResidue.PreparedGeneration, abandoned, ConfigurationTerminalState.Abandoned),
                new ConfigurationRepair(ConfigurationResidue.EffectivePointer, effective, ConfigurationTerminalState.Confirmed),
                new ConfigurationRepair(ConfigurationResidue.EvidenceOutbox, "intent-2", ConfigurationTerminalState.Published),
            ],
            result.Record.Repairs);
        // The pointer is confirmed, never moved.
        Assert.Equal(effective, Pointer().Digest);
        Assert.Equal(activatedAt, Pointer().ActivatedAt);
        using var context = _db.CreateContext();
        Assert.Equal([effective], context.PreparedProjections.AsNoTracking().Select(row => row.CandidateDigest).ToList());
        var outbox = context.EvidenceOutbox.AsNoTracking().ToDictionary(row => row.IntentId, row => row.PublishedAt);
        Assert.Equal(Frozen, outbox["intent-1"]);
        Assert.Equal(Recovered, outbox["intent-2"]);
        var record = Assert.Single(context.Recoveries.AsNoTracking());
        Assert.Equal("Node crashed before publishing intent-2", record.Reason);
        var audit = Assert.Single(context.RecoveryAudit.AsNoTracking());
        Assert.Equal("data-directory-owner:test", audit.ActorId);
        Assert.Equal(Recovered, audit.RecordedAt);
        Assert.Contains(KernelProfile.ConfigurationRecovery, audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(record.RecoveryId, audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_refuses_without_reason_naming_the_state()
    {
        await CrashResidueAsync();

        var result = await RecoverAsync(" ");

        Assert.False(result.Committed);
        Assert.Equal(KernelRecoveryErrors.ReasonRequired, result.Refusal!.Code);
        Assert.Equal("reason-absent", result.Refusal.State);
        AssertNothingRecovered();
    }

    [Fact]
    public async Task Recover_refuses_once_the_data_directory_lock_is_released()
    {
        await CrashResidueAsync();
        await _runLock.DisposeAsync();

        var result = await RecoverAsync("Node crashed");

        Assert.Equal(KernelRecoveryErrors.CapabilityRequired, result.Refusal!.Code);
        Assert.Equal(KernelProfile.ConfigurationRecovery, result.Refusal.State);
        AssertNothingRecovered();
    }

    [Theory]
    [InlineData("record-written")]
    [InlineData("audit-written")]
    public async Task Recover_commits_record_and_audit_in_one_transaction(string point)
    {
        await CrashResidueAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoverAsync("Node crashed",
            at => { if (at == point) throw new InvalidOperationException("crash:" + at); }));

        AssertNothingRecovered();
        // The crash left nothing half-done, so the same recovery runs to completion afterwards.
        Assert.True((await RecoverAsync("Node crashed")).Committed);
    }

    [Fact]
    public async Task Verb_composition_resolves_the_evidence_delivery_over_the_durable_trail()
    {
        // The verb's own store registration, so the trail it resolves is the one the running host reads.
        var rootSeed = RandomNumberGenerator.GetBytes(32);
        var services = new ServiceCollection();
        services.AddSqlCipherLocalNodeDbContext(rootSeed: rootSeed, databasePath: Path.Combine(_dataDirectory, "local-node.db"),
            keyDerivation: new SqlCipherKeyDerivation());
        ConfigurationRecoveryCommand.AddEvidenceDelivery(services, rootSeed, new FixedTime(Recovered));
        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ConfigurationEvidenceOutbox>());
        Assert.IsType<Harborline.Api.Kernel.Audit.AuthorityCapturingAuditTrail>(
            provider.GetRequiredService<Harborline.Api.Kernel.Audit.IAuditTrail>());
    }

    [Fact]
    public async Task Recover_appends_a_stranded_entry_that_carries_its_authority_to_the_trail()
    {
        await CrashResidueAsync();
        Assert.Equal(0, await OnTrailAsync("intent-2"));

        var result = await RecoverAsync("Node crashed before publishing intent-2");

        Assert.True(result.Committed, result.Refusal?.Code);
        var entry = Assert.Single(await EntriesAsync("intent-2"));
        Assert.Equal("test:operator", entry.AuthoritySnapshot!.Principal);
        Assert.Empty(StandIns());
    }

    [Fact]
    public async Task Recover_does_not_append_a_pre_migration_entry_and_its_audit_names_it()
    {
        await CrashResidueAsync();
        using (var context = _db.CreateContext())
            await context.EvidenceOutbox.Where(row => row.IntentId == "intent-2")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AuthoritySnapshotJson, (string?)null));

        var result = await RecoverAsync("Node crashed before the authority column existed");

        Assert.True(result.Committed, result.Refusal?.Code);
        Assert.Equal(0, await OnTrailAsync("intent-2"));
        Assert.Equal(["intent-2"], StandIns());
        using var read = _db.CreateContext();
        Assert.Equal(Recovered, read.EvidenceOutbox.AsNoTracking().Single(row => row.IntentId == "intent-2").PublishedAt);
    }

    [Theory]
    [InlineData("record-written")]
    [InlineData("audit-written")]
    public async Task Recover_run_again_after_a_crash_appends_once(string point)
    {
        await CrashResidueAsync();
        var before = await TrailCountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoverAsync("Node crashed",
            at => { if (at == point) throw new InvalidOperationException("crash:" + at); }));

        Assert.True((await RecoverAsync("Node crashed")).Committed);
        Assert.Equal(0, await _drainer.DrainAsync());

        Assert.Equal(before + 1, await TrailCountAsync());
        Assert.Equal(1, await OnTrailAsync("intent-2"));
    }

    [Fact]
    public async Task Recover_crashed_then_drained_appends_once()
    {
        await CrashResidueAsync();
        var before = await TrailCountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoverAsync("Node crashed",
            at => { if (at == "audit-written") throw new InvalidOperationException("crash:" + at); }));

        Assert.Equal(1, await _drainer.DrainAsync());

        Assert.Equal(before + 1, await TrailCountAsync());
        Assert.Equal(1, await OnTrailAsync("intent-2"));
        using var context = _db.CreateContext();
        Assert.All(context.EvidenceOutbox.AsNoTracking(), row => Assert.NotNull(row.PublishedAt));
    }

    private async Task<List<Harborline.Api.Kernel.Audit.AuditRecord>> EntriesAsync(string intentId)
    {
        var entries = new List<Harborline.Api.Kernel.Audit.AuditRecord>();
        await foreach (var record in _trail.QueryAsync(new Harborline.Api.Kernel.Audit.AuditQuery(Tenant,
            AuditId: ConfigurationEvidenceOutbox.AuditIdFor(Tenant.Value, intentId))))
            entries.Add(record);
        return entries;
    }

    private async Task<int> OnTrailAsync(string intentId) => (await EntriesAsync(intentId)).Count;

    /// <summary>Every entry on the tenant's trail, whatever its id: a duplicate under a fresh id still counts.</summary>
    private async Task<int> TrailCountAsync()
    {
        var count = 0;
        await foreach (var _ in _trail.QueryAsync(new Harborline.Api.Kernel.Audit.AuditQuery(Tenant))) count++;
        return count;
    }

    /// <summary>The evidence intents the one recovery audit names as stood in for, not appended.</summary>
    private string[] StandIns()
    {
        using var context = _db.CreateContext();
        var audit = Assert.Single(context.RecoveryAudit.AsNoTracking());
        using var payload = System.Text.Json.JsonDocument.Parse(audit.PayloadJson);
        return payload.RootElement.TryGetProperty("standInFor", out var standIns)
            ? standIns.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : ["<absent>"];
    }

    private ValueTask<ConfigurationRecoveryResult<ConfigurationRecoveryRecord>> RecoverAsync(string reason, Action<string>? crashPoint = null) =>
        ConfigurationRecoveryCommand.RecoverAsync(_db.Factory, new KernelClock(new FixedTime(Recovered)),
            new ConfigurationRecoveryRequest(Guid.NewGuid().ToString("N"), Tenant.Value, "data-directory-owner:test", reason, "{\"authority\":\"data-directory-ownership\"}"),
            new ConfigurationRecoveryCommand.DataDirectoryOwnership(_runLock), _verbEvidence, crashPoint);

    /// <summary>Two activations; the second crashes before publication, leaving the first candidate prepared but not effective.</summary>
    private async Task<(string Abandoned, string Effective)> CrashResidueAsync()
    {
        var first = await ActivateAsync("intent-1", "acme.ext");
        _target.CrashPoint = at => { if (at == "before-publish") throw new InvalidOperationException("crash:publish"); };
        var second = await ActivateAsync("intent-2", "acme.core");
        _target.CrashPoint = null;
        return (first, second);
    }

    private void AssertNothingRecovered()
    {
        using var context = _db.CreateContext();
        Assert.Empty(context.Recoveries.AsNoTracking());
        Assert.Empty(context.RecoveryAudit.AsNoTracking());
        Assert.Equal(2, context.PreparedProjections.AsNoTracking().Count());
        Assert.Null(context.EvidenceOutbox.AsNoTracking().Single(row => row.IntentId == "intent-2").PublishedAt);
    }

    private ConfigurationEffectiveGenerationRow Pointer()
    {
        using var context = _db.CreateContext();
        return context.EffectiveGenerations.AsNoTracking().Single();
    }

    private async Task<string> ActivateAsync(string intentId, string sharedOwner)
    {
        var baseline = _target.ReadEffective(Tenant).Digest;
        var prepared = _target.Prepare(Tenant, baseline, ["acme.core", "acme.ext", CmPack],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["form.shared"] = sharedOwner }, Frozen);
        var authority = new AuthorizationWriteContext(new ActorId("test:operator"), Tenant, Frozen);
        var outcome = await _target.For(authority).CompareAndSwapAsync(
            new(prepared.Preparation!.Prepared!, "test:operator", new ConfigurationEvidenceIntent(intentId, "T-909 recovery test")));
        Assert.Null(outcome.Decision.Refusal);
        return prepared.Preparation.Prepared!.Candidate.Digest;
    }

    private void Seed(string packKey, string[] contentKeys)
    {
        var seeds = contentKeys.Select(key => new PackSeedItem(key, PackContentKind.FormDefinition, "1",
            $$"""{"id":"{{key}}","pack":"{{packKey}}"}""", Cid.FromBytes(Encoding.UTF8.GetBytes(key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        _store.Commit(new PackInstallTransaction(Tenant, pack, new PackInstallWatermark(packKey, "1.0.0", new Dictionary<string, int>()), []));
        _store.Activate(Tenant, packKey, "1.0.0");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
