using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

using static Harborline.Api.Kernel.Runtime.WritePipelineStage;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-1048 (DES-0029 ck-6): pack install, activation, deactivation and (T-1048b) narrowing stage their audit entry in the SAME
/// transaction as the durable pack change. A process that dies after the commit and before react leaves the entry
/// owed in the audit outbox; a restarted host over the same encrypted file delivers it exactly once. A write refused
/// at commit stages nothing.
/// </summary>
public sealed class PackAuditOutboxCrashTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000001048");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string PackKey = "test.t1048";
    private const string Operator = "test-operator";
    private const string EventType = "PackComposerInstall";

    private readonly KeyPair _keys = KeyPair.Generate();
    private readonly NodePrincipalSigner _nodeSigner = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private readonly CrashingObserver _stages = new();
    private DurableAuditHarness _harness = null!;
    private DurablePackInstallStore _store = null!;
    private PackInstaller _installer = null!;

    public async Task InitializeAsync()
    {
        _harness = await DurableAuditHarness.CreateAsync();
        await using (var packs = _harness.Store.CreatePacksContext())
            await packs.Database.MigrateAsync();
        (_store, _installer) = Compose(_harness, projector: null);
        PlatformPackTestPreload.Activate(_store, Tenant);
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
        _nodeSigner.Dispose();
        _keys.Dispose();
    }

    [Fact(DisplayName = "T-1048: a crash after the install commit leaves its audit owed, and a restarted host delivers it once")]
    public async Task A_crash_after_the_install_commit_is_delivered_once_after_restart()
    {
        var bytes = await PackAsync();
        _stages.CrashAt = React;

        await Assert.ThrowsAsync<SimulatedCrash>(() => _installer.InstallAsync(bytes, Context()));

        await AssertOwedAndUndeliveredAsync("Installed");
        await using var restarted = _harness.Reopen();
        Assert.Equal(PackLifecycleState.Draft, new DurablePackInstallStore(restarted.Store.PacksFactory).GetVersion(Tenant, PackKey, "1.0.0")?.Lifecycle);
        await AssertDeliveredOnceAsync(restarted, "Installed");
    }

    [Fact]
    public async Task A_failed_append_does_not_copy_untrusted_pack_coordinates_to_the_log()
    {
        await _harness.ExecuteAsync("CREATE TRIGGER t1048_log_fault BEFORE INSERT ON search_audit_trail BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END;");
        var logger = new AuditLogger();
        var adapter = new KernelAuditPackInstallAudit(_harness.Trail, _nodeSigner, logger);

        adapter.Append(new PackInstallAuditEntry(Tenant, PackInstallAuditAction.Refused,
            "untrusted-pack-coordinate", "1.0.0\r\nFORGED AUDIT MESSAGE", Now, null, null, "refused", PreDecision: true));

        var message = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Error, message.Level);
        Assert.Equal("Pack install audit append FAILED - the mutation stands but its durable audit envelope was not written.", message.Text);
    }

    private sealed class AuditLogger : ILogger<KernelAuditPackInstallAudit>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tied_time_ceremony_precedes_its_install_after_restart(bool failCeremony)
    {
        var decision = TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, Operator, Now);
        var audit = new KernelAuditPackInstallAudit(_harness.Trail, _nodeSigner, NullLogger<KernelAuditPackInstallAudit>.Instance);
        var store = new DurablePackInstallStore(_harness.Store.PacksFactory, audit);
        store.Commit(new PackInstallTransaction(Tenant,
            new InstalledPack(PackKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, [],
                new Dictionary<string, int>(), Now, _keys.PrincipalId, 1, TrustScope.OwnRoster, []),
            new PackInstallWatermark(PackKey, "1.0.0", new Dictionary<string, int>()), [])
        {
            Audit = new PackCommitAudit([
                new PackInstallAuditEntry(Tenant, PackInstallAuditAction.BreakGlassOverride, PackKey, "1.0.0", Now,
                    null, null, "override", BreakGlassJustification: "approved recovery", BreakGlassAuthorizingPrincipal: Operator, ActingPrincipal: Operator),
                new PackInstallAuditEntry(Tenant, PackInstallAuditAction.Installed, PackKey, "1.0.0", Now,
                    null, null, "installed", ActingPrincipal: Operator),
            ], decision),
        });

        // Put the dependent first in both identifier and physical insertion order. Neither is the ceremony order.
        await using (var db = _harness.Store.CreateContext())
        {
            var rows = await db.AuditOutbox.Where(row => row.EventType == EventType).ToListAsync();
            var ceremony = Assert.Single(rows, row => Action(row.BodyJson) == "BreakGlassOverride");
            var installed = Assert.Single(rows, row => Action(row.BodyJson) == "Installed");
            Assert.Equal(ceremony.AuditId, installed.PredecessorAuditId);
            db.AuditOutbox.RemoveRange(rows);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            ceremony.AuditId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
            installed.AuditId = "00000000-0000-0000-0000-000000000001";
            installed.PredecessorAuditId = ceremony.AuditId;
            db.AuditOutbox.AddRange(installed, ceremony);
            await db.SaveChangesAsync();
        }
        if (failCeremony)
        {
            await using var db = _harness.Store.CreateContext();
            audit.Stage(db, new PackInstallAuditEntry(Tenant, PackInstallAuditAction.Activated, "independent-pack", "1.0.0",
                Now, null, null, "activated", ActingPrincipal: Operator),
                TestAuthorization.AllowedDecision(Tenant, "independent-pack", "pack", Permission.PackagesOperate, Operator, Now));
            await db.SaveChangesAsync();
            await _harness.ExecuteAsync("CREATE TRIGGER t1048_ceremony_fault BEFORE INSERT ON search_audit_trail WHEN NEW.record_json LIKE '%BreakGlassOverride%' BEGIN SELECT RAISE(ABORT, 'ceremony unavailable'); END;");
        }

        await using var restarted = _harness.Reopen();
        var recorded = new RecordingTrail(restarted.Trail);
        using var drain = new NodeAuditOutbox(restarted.Store.Factory, recorded, recorded,
            new Ed25519Signer(_keys), TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
        if (failCeremony)
        {
            Assert.Equal(1, await drain.DrainAsync());
            Assert.Equal(["Activated"], recorded.Actions);
            Assert.DoesNotContain(await TrailActionsAsync(restarted), action => action is "BreakGlassOverride" or "Installed");
            await using var db = restarted.Store.CreateContext();
            Assert.Equal(2, await db.AuditOutbox.CountAsync(row => row.PublishedAtUnixMs == null));
            await restarted.ExecuteAsync("DROP TRIGGER t1048_ceremony_fault;");
            recorded.Actions.Clear();
        }
        Assert.Equal(2, await drain.DrainAsync());
        Assert.Equal(["BreakGlassOverride", "Installed"], recorded.Actions);
        Assert.Equal(0, await drain.DrainAsync());
        var persisted = await TrailActionsAsync(restarted);
        Assert.Single(persisted, action => action == "BreakGlassOverride");
        Assert.Single(persisted, action => action == "Installed");
    }

    private sealed class RecordingTrail(AuthorityCapturingAuditTrail inner) : IAuditTrail, ICapturedAuditTrail
    {
        public List<string?> Actions { get; } = [];
        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            await inner.AppendAsync(record, ct);
            Actions.Add(Body(record, "action"));
        }
        public async ValueTask AppendCapturedAsync(AuditRecord record, CancellationToken ct = default)
        {
            await inner.AppendCapturedAsync(record, ct);
            Actions.Add(Body(record, "action"));
        }
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);
    }

    [Fact(DisplayName = "T-1048: a crash after the activation commit leaves its audit owed, and a restarted host delivers it once")]
    public async Task A_crash_after_the_activation_commit_is_delivered_once_after_restart()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync(), Context())).Installed);
        _stages.CrashAt = React;

        await Assert.ThrowsAsync<SimulatedCrash>(() => _installer.ActivateAsync(Context(), PackKey, "1.0.0"));

        await AssertOwedAndUndeliveredAsync("Activated");
        await using var restarted = _harness.Reopen();
        Assert.Equal("1.0.0", new DurablePackInstallStore(restarted.Store.PacksFactory).GetActive(Tenant, PackKey)?.Version);
        await AssertDeliveredOnceAsync(restarted, "Activated");
    }

    [Fact(DisplayName = "T-1048: a crash after the deactivation commit leaves its audit owed, and a restarted host delivers it once")]
    public async Task A_crash_after_the_deactivation_commit_is_delivered_once_after_restart()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync(), Context())).Installed);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        _stages.CrashAt = React;

        await Assert.ThrowsAsync<SimulatedCrash>(() => _installer.DeactivateAsync(Context(), PackKey, "1.0.0"));

        await AssertOwedAndUndeliveredAsync("Deactivated");
        await using var restarted = _harness.Reopen();
        var reopened = new DurablePackInstallStore(restarted.Store.PacksFactory);
        Assert.Null(reopened.GetActive(Tenant, PackKey));
        Assert.Equal(PackLifecycleState.Inactive, reopened.GetVersion(Tenant, PackKey, "1.0.0")?.Lifecycle);
        await AssertDeliveredOnceAsync(restarted, "Deactivated");
    }

    [Fact(DisplayName = "T-1048: an install whose commit fails stages no audit and records none")]
    public async Task An_install_refused_at_commit_stages_nothing()
    {
        var bytes = await PackAsync();
        await _harness.ExecuteAsync(
            "CREATE TRIGGER t1048_fault BEFORE INSERT ON pack_installed_versions WHEN NEW.pack_key = 'test.t1048' "
            + "BEGIN SELECT RAISE(ABORT, 't1048'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => _installer.InstallAsync(bytes, Context()));

        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Empty(await OutboxActionsAsync(_harness));
        Assert.Empty(await TrailActionsAsync(_harness));
    }

    [Fact(DisplayName = "T-1048: a fault staging the install audit rolls the install back")]
    public async Task A_fault_staging_the_install_audit_rolls_the_install_back()
    {
        var bytes = await PackAsync();
        await _harness.ExecuteAsync("CREATE TRIGGER t1048_fault BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 't1048'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => _installer.InstallAsync(bytes, Context()));

        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(_store.GetWatermark(Tenant, PackKey));
        Assert.Empty(await TrailActionsAsync(_harness));
    }

    [Fact(DisplayName = "T-1048: an activation whose projection is refused at commit stages no audit and records only the refusal")]
    public async Task An_activation_refused_at_commit_stages_nothing()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync(), Context())).Installed);
        var (store, installer) = Compose(_harness, new RefusingProjector());

        var outcome = await installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.Equal(PackInstallCodes.ActivateProjectionRefused, outcome.Error);
        Assert.Null(store.GetActive(Tenant, PackKey));
        Assert.Equal(["Installed"], await OutboxActionsAsync(_harness));
        Assert.Equal(["Installed", "Refused"], await TrailActionsAsync(_harness));
    }

    [Fact(DisplayName = "T-1048: a deactivation refused at commit by a dependent stages no audit and records only the refusal")]
    public async Task A_deactivation_refused_at_commit_stages_nothing()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync(), Context())).Installed);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        const string dependent = "test.t1048-dependent";
        _stages.OnCommit = () =>
        {
            _store.Commit(new PackInstallTransaction(Tenant,
                new InstalledPack(dependent, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, [],
                    new Dictionary<string, int>(), Now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
                    TrustScope.OwnRoster, [new PackDependencyRef(PackKey, "1.0.0")]),
                new PackInstallWatermark(dependent, "1.0.0", new Dictionary<string, int>()), []));
            _store.Activate(Tenant, dependent, "1.0.0");
        };

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
        Assert.Equal(["Activated", "Installed"], await OutboxActionsAsync(_harness));
        Assert.Equal(["Installed", "Activated", "Refused"], await TrailActionsAsync(_harness));
    }

    [Fact(DisplayName = "T-1048b: a crash after the narrowing commit leaves its audit owed, and a restarted host delivers it once")]
    public async Task A_crash_after_the_narrowing_commit_is_delivered_once_after_restart()
    {
        await InstallAndActivateAsync();
        _stages.CrashAt = React;

        await Assert.ThrowsAsync<SimulatedCrash>(() => NarrowAsync());

        await AssertOwedAndUndeliveredAsync("Narrowed");
        await using var restarted = _harness.Reopen();
        var stored = Assert.Single(new DurablePackInstallStore(restarted.Store.PacksFactory).GetOverrides(Tenant, PackKey));
        Assert.Equal(("t1048-form", "{\"title\":null}"), (stored.ContentKey, stored.OverlayPatch.ToJsonString()));
        await AssertDeliveredOnceAsync(restarted, "Narrowed");
    }

    [Fact(DisplayName = "T-1048b: a narrowing whose commit fails stages no audit and records none")]
    public async Task A_narrowing_refused_at_commit_stages_nothing()
    {
        await InstallAndActivateAsync();
        await _harness.ExecuteAsync("CREATE TRIGGER t1048b_fault BEFORE INSERT ON pack_overrides BEGIN SELECT RAISE(ABORT, 't1048b'); END;");

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => NarrowAsync());
        var sqlite = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Equal(19, sqlite.SqliteErrorCode);
        Assert.Equal(1811, sqlite.SqliteExtendedErrorCode);
        Assert.Contains("t1048b", sqlite.Message, StringComparison.Ordinal);

        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
        Assert.Equal(["Activated", "Installed"], await OutboxActionsAsync(_harness));
        Assert.Equal(["Installed", "Activated"], await TrailActionsAsync(_harness));
    }

    private async Task InstallAndActivateAsync()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync(), Context())).Installed);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
    }

    private Task<PackNarrowingOutcome> NarrowAsync() => _installer.NarrowAsync(
        Context(), PackKey, "t1048-form", JsonNode.Parse("""{"title":null}""")!,
        TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now));

    private (DurablePackInstallStore Store, PackInstaller Installer) Compose(DurableAuditHarness harness, IPackProjectionDispatcher? projector)
    {
        var audit = new KernelAuditPackInstallAudit(harness.Trail, _nodeSigner, NullLogger<KernelAuditPackInstallAudit>.Instance, harness.Outbox);
        var store = new DurablePackInstallStore(harness.Store.PacksFactory, audit);
        var verifier = new PackVerifier(new Ed25519Verifier(), new PackFileCodec());
        var installer = projector is null
            ? new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(), audit,
                TestAuthorization.AllowGate(), pipelineObserver: _stages)
            : new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(), audit,
                TestAuthorization.AllowGate(), projector, pipelineObserver: _stages);
        return (store, installer);
    }

    /// <summary>The crashed process wrote the entry to the outbox and delivered nothing.</summary>
    private async Task AssertOwedAndUndeliveredAsync(string action)
    {
        await using var db = _harness.Store.CreateContext();
        var owed = await db.AuditOutbox.AsNoTracking().Where(row => row.EventType == EventType && row.PublishedAtUnixMs == null).ToListAsync();
        Assert.Equal([action], owed.Select(row => Action(row.BodyJson)));
        Assert.DoesNotContain(action, await TrailActionsAsync(_harness));
    }

    /// <summary>The restarted host's drain delivers the owed entry once; a second drain adds nothing.</summary>
    private static async Task AssertDeliveredOnceAsync(DurableAuditHarness restarted, string action)
    {
        await restarted.Outbox.DrainAsync();
        await restarted.Outbox.DrainAsync();

        var records = await TrailAsync(restarted);
        var record = Assert.Single(records, record => Body(record, "action") == action);
        Assert.Equal(PackKey, Body(record, "packKey"));
        Assert.Equal("1.0.0", Body(record, "version"));
        Assert.Equal(new ActorId(Operator), record.Actor);
        Assert.Equal(Now, record.OccurredAt);
        Assert.NotNull(record.AuthoritySnapshot);
        await using var db = restarted.Store.CreateContext();
        Assert.Empty(await db.AuditOutbox.AsNoTracking().Where(row => row.PublishedAtUnixMs == null).ToListAsync());
    }

    private static async Task<List<string>> OutboxActionsAsync(DurableAuditHarness harness)
    {
        await using var db = harness.Store.CreateContext();
        var rows = await db.AuditOutbox.AsNoTracking().Where(row => row.EventType == EventType).ToListAsync();
        // Every entry carries the fixed clock's instant, so the rows are compared as a sorted set.
        return rows.Select(row => Action(row.BodyJson)).Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<List<string?>> TrailActionsAsync(DurableAuditHarness harness) =>
        (await TrailAsync(harness)).Select(record => Body(record, "action")).ToList();

    private static async Task<List<AuditRecord>> TrailAsync(DurableAuditHarness harness)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in harness.Trail.QueryAsync(new AuditQuery(Tenant, new AuditEventType(EventType))))
            records.Add(record);
        return records;
    }

    private static string Action(string bodyJson) =>
        JsonDocument.Parse(bodyJson).RootElement.GetProperty("action").GetString()!;

    private static string? Body(AuditRecord record, string key) =>
        record.Payload.Payload.Body.TryGetValue(key, out var value) ? value?.ToString() : null;

    private PackInstallContext Context() => new(
        Tenant,
        new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, _keys.PrincipalId, 1, TrustRootStatus.Current)]),
        PackRevocationList.Empty, Now, TimeSpan.FromDays(30), Principal: Operator);

    private async Task<byte[]> PackAsync()
    {
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec,
            TimeProvider.System);
        var export = await exporter.ExportAsync(new PackExportRequest(
            PackKey, "1.0.0", "T-1048 audit outbox", "Audit outbox fixture",
            PackScopeTier.Horizontal,
            [new PackContentSource("t1048-form", PackContentKind.FormDefinition, "1.0.0", new JsonObject { ["title"] = "t1048" })],
            Array.Empty<PackDependencyRef>(),
            Array.Empty<string>(),
            1,
            Dcp: DomainComplianceProfile.General("t1048-author")), new Ed25519Signer(_keys));
        Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(error => error.Code)));
        return export.FileBytes!;
    }

#pragma warning disable CA1032, CA1064 // A test-only stand-in for process death.
    private sealed class SimulatedCrash : Exception;
#pragma warning restore CA1032, CA1064

    /// <summary>Kills the write at a stage (process death after commit when that stage is react).</summary>
    private sealed class CrashingObserver : IWritePipelineObserver
    {
        public WritePipelineStage? CrashAt { get; set; }

        public Action? OnCommit { get; set; }

        public void OnStage(WritePipelineStage stage)
        {
            if (stage == Commit) OnCommit?.Invoke();
            if (stage == CrashAt) throw new SimulatedCrash();
        }
    }

    private sealed class RefusingProjector : IPackProjectionDispatcher
    {
        public void StageProjection(PackProjectionTransaction transaction) { }

        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default) => new Refusal();

        private sealed class Refusal : IPackProjectionRefusalReport
        {
            public bool ProjectionRefused => true;
        }
    }
}
