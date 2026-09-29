using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

/// <summary>
/// DES-0029 kernel-core-ck-6 at the authorization configuration commit: the audit entry commits in the SAME
/// transaction as the write (real keyed search store; faults are SQLite triggers on the real tables), and the
/// outbox delivers it to the kernel trail exactly once, with the authority the gate decided.
/// </summary>
public sealed class NodeAuditOutboxTests : IAsyncLifetime
{
    private static readonly RoleReference OutboxRole = new(RoleVocabularies.Domain, "outbox-author");
    private static readonly TenantId Tenant = TenantId.FromString("tenant-outbox");
    private static readonly ActorId Admin = new("admin-outbox");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
    private static readonly AuthorizationCapabilityDefinition Definition = new(
        new AuthorizationCapabilityDefinitionId(Guid.Parse("eeeeeeee-0000-0000-0000-000000000006")),
        "package.outbox",
        1,
        AuthorizationOperation.Parse(Permission.OrgManageSettings),
        PermissionAtom.Parse($"{Permission.OrgManageSettings}@/"),
        RoleBindingSet.Of(OutboxRole));

    private SearchTestStore _store = null!;
    private AuthorizationDefinitionWriter _writer = null!;
    private InMemoryAuditTrail _trail = null!;
    private NodeAuditOutbox _outbox = null!;

    public async Task InitializeAsync()
    {
        _store = await SearchTestStore.CreateAsync();
        var vocabulary = new InMemoryRoleVocabulary(
            [RoleDefinition.CreatePackageRole(RoleDefinitionId.New(), OutboxRole.Name, "Outbox author", "package.outbox")]);
        var configuration = new NodeEfAuthorizationConfigurationStore(_store.Factory, vocabulary);
        _writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            new NodeEfGrantStore(_store.Factory));
        _trail = new InMemoryAuditTrail();
        _outbox = new NodeAuditOutbox(_store.Factory, _trail, _trail, new Ed25519Signer(KeyPair.Generate()),
            TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact(DisplayName = "ck-6 outbox: a binding narrowing commits its audit entry in the same transaction, delivered once with its decision")]
    public async Task BindingNarrowing_CommitsItsAuditAndDeliversItOnce()
    {
        await InstallAsync();
        var result = await NarrowAsync();

        var row = await SingleOwedAsync(NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType);
        Assert.Equal(result.AuditId?.ToString("D"), row.AuditId);
        Assert.NotNull(row.AuthoritySnapshotJson);

        await _outbox.DrainAsync();
        await _outbox.DrainAsync();

        var delivered = Assert.Single(await EntriesAsync(NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType));
        Assert.Equal(result.AuditId, delivered.AuditId);
        Assert.Equal(Admin, delivered.Actor);
        Assert.Equal(result.Decision!.Request.Act, delivered.Act);
        Assert.NotNull(delivered.AuthoritySnapshot);
        Assert.Empty(await OwedAsync());
    }

    [Fact(DisplayName = "ck-6 outbox: a fault staging the audit entry rolls the binding narrowing back")]
    public async Task AuditStagingFault_RollsTheBindingBack()
    {
        await InstallAsync();
        await ExecuteAsync("CREATE TRIGGER ck6_fault BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'ck6'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => NarrowAsync());

        await using var db = _store.CreateContext();
        Assert.Empty(await db.AuthorizationBindingRevisions.ToListAsync());
        Assert.DoesNotContain(await db.AuditOutbox.ToListAsync(),
            row => row.EventType == NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType.Value);
    }

    [Fact(DisplayName = "ck-6 outbox: a crash after the trail append and before the mark delivers no second entry")]
    public async Task CrashBeforeMark_IsNotAppendedTwice()
    {
        await InstallAsync();
        var result = await NarrowAsync();
        await ExecuteAsync("CREATE TRIGGER ck6_fault BEFORE UPDATE ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'ck6'); END;");

        // Recording the failure also fails (the same trigger); the pass still finishes instead of throwing.
        Assert.Equal(0, await _outbox.DrainAsync());
        await ExecuteAsync("DROP TRIGGER ck6_fault;");
        Assert.Single(await EntriesAsync(NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType));
        Assert.NotEmpty(await OwedAsync());

        await _outbox.DrainAsync();

        Assert.Equal(result.AuditId, Assert.Single(await EntriesAsync(NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType)).AuditId);
        Assert.Empty(await OwedAsync());
    }

    [Fact(DisplayName = "ck-6 outbox: two overlapping drains deliver an owed entry once")]
    public async Task OverlappingDrains_DeliverTheEntryOnce()
    {
        await InstallAsync();
        var result = await NarrowAsync();
        // Both passes meet inside the "does the trail hold it?" check, so an unserialized drain appends twice.
        var outbox = new NodeAuditOutbox(_store.Factory, new RendezvousTrail(_trail), _trail,
            new Ed25519Signer(KeyPair.Generate()), TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);

        await Task.WhenAll(Task.Run(() => outbox.DrainAsync()), Task.Run(() => outbox.DrainAsync()));

        Assert.Equal(result.AuditId, Assert.Single(await EntriesAsync(NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType)).AuditId);
        Assert.Empty(await OwedAsync());
    }

    [Fact(DisplayName = "ck-6 outbox: a failed delivery stays owed and is delivered by the next drain")]
    public async Task FailedDelivery_StaysOwedUntilTheNextDrain()
    {
        await InstallAsync();
        var result = await NarrowAsync();
        var failing = new NodeAuditOutbox(_store.Factory, _trail, new FailingCapturedTrail(),
            new Ed25519Signer(KeyPair.Generate()), TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);

        Assert.Equal(0, await failing.DrainAsync());
        var owed = Assert.Single(await OwedAsync());
        Assert.Equal(1, owed.Attempts);
        Assert.NotNull(owed.LastError);

        await _outbox.DrainAsync();
        Assert.Equal(result.AuditId, Assert.Single(await EntriesAsync(NodeEfAuthorizationConfigurationStore.BindingNarrowedEventType)).AuditId);
    }

    [Fact(DisplayName = "ck-6 outbox: a pack's projected definition commits a system audit entry attributed to the pack")]
    public async Task PackDefinition_CommitsASystemAuditEntry()
    {
        var principal = new ActorId("pack-installer");
        var scope = ScopeExpression.Parse("/records/package.outbox");
        var decision = await TestAuthorization.AllowGate().DecideAsync(new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            principal, Tenant, new AuthorizationTarget("pack", "package.outbox", scope), At));

        var seedRoles = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var configuration = new NodeEfAuthorizationConfigurationStore(_store.Factory, seedRoles);
        var writer = new AuthorizationDefinitionWriter(configuration, configuration,
            new AuthorizationDefinitionAdmission(seedRoles), new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(), new NodeEfGrantStore(_store.Factory));
        var packDefinition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("eeeeeeee-0000-0000-0000-000000000007")),
            "package.outbox", 1, AuthorizationOperation.Parse(Permission.ContactsRead),
            PermissionAtom.Parse($"{Permission.ContactsRead}@/"), RoleBindingSet.Of(RoleReference.Administrator));

        await writer.WritePackDefinitionAsync(
            packDefinition, new Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority(
                decision, "package.outbox", "1.0.0", Tenant, principal, At));

        var row = await SingleOwedAsync(NodeEfAuthorizationConfigurationStore.DefinitionWrittenEventType);
        Assert.Equal("pack:package.outbox", row.Actor);
        Assert.Null(row.AuthoritySnapshotJson);
        await _outbox.DrainAsync();
        Assert.Single(await EntriesAsync(NodeEfAuthorizationConfigurationStore.DefinitionWrittenEventType));
    }

    [Fact(DisplayName = "ck-6 outbox: an admission conferral commits its audit with the grant; a staging fault leaves neither")]
    public async Task AdmissionConferral_CommitsItsAuditWithTheGrant()
    {
        var configuration = new NodeEfAuthorizationConfigurationStore(_store.Factory, new InMemoryRoleVocabulary([]));
        var permissions = PermissionSet.From([Permission.OrgManageSettings]);
        await ExecuteAsync("CREATE TRIGGER ck6_fault BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'ck6'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            configuration.ConferAdmissionGrantAsync(Tenant, "party-admitted", Admin.Value, permissions, At));
        await using (var db = _store.CreateContext())
            Assert.Empty(await db.Grants.ToListAsync());

        await ExecuteAsync("DROP TRIGGER ck6_fault;");
        var grant = await configuration.ConferAdmissionGrantAsync(Tenant, "party-admitted", Admin.Value, permissions, At);

        var row = await SingleOwedAsync(NodeEfAuthorizationConfigurationStore.AdmissionGrantConferredEventType);
        Assert.Contains(grant!.GrantId.ToString(), row.BodyJson, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ck-6 outbox: the retired-actor rekey commits one audit entry per moved grant in its fence")]
    public async Task RetiredActorRekey_CommitsItsAudit()
    {
        var configuration = new NodeEfAuthorizationConfigurationStore(_store.Factory, new InMemoryRoleVocabulary([]));
        await configuration.ConferAdmissionGrantAsync(Tenant,
            Harborline.Api.LocalNodeHost.Data.Identity.NodeOperatorIdentity.RetiredDesktopActor, Admin.Value,
            PermissionSet.From([Permission.OrgManageSettings]), At);
        await _outbox.DrainAsync();

        Assert.Equal(1, await RetiredDesktopActorRekey.RunAsync(_store.Factory, new ActorId("party-operator"), At, CancellationToken.None));

        var row = await SingleOwedAsync(RetiredDesktopActorRekey.RekeyedEventType);
        Assert.Equal("party-operator", row.Actor);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task InstallAsync()
    {
        await _writer.WriteAsync(new InstallAuthorizationDefinition(Definition), new AuthorizationWriteContext(Admin, Tenant, At));
        // The install's own entry is delivered first, so each test observes only the act it performs.
        Assert.Equal(1, await _outbox.DrainAsync());
    }

    private async Task<AuthorizationConfigurationWriteResult> NarrowAsync() =>
        await _writer.WriteAsync(
            new NarrowCapabilityRoleBinding(Tenant, Definition.DefinitionId, RoleBindingSet.Empty, Admin, At,
                new BindingChangeReason("outbox test")),
            new AuthorizationWriteContext(Admin, Tenant, At));

    private async Task<AuditOutboxRow> SingleOwedAsync(AuditEventType eventType) =>
        Assert.Single(await OwedAsync(), row => row.EventType == eventType.Value);

    private async Task<List<AuditOutboxRow>> OwedAsync()
    {
        await using var db = _store.CreateContext();
        return await db.AuditOutbox.AsNoTracking().Where(row => row.PublishedAtUnixMs == null).ToListAsync();
    }

    private async Task<List<AuditRecord>> EntriesAsync(AuditEventType eventType)
    {
        var entries = new List<AuditRecord>();
        await foreach (var record in _trail.QueryAsync(new AuditQuery(Tenant, eventType)))
            entries.Add(record);
        return entries;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = _store.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed class RendezvousTrail(InMemoryAuditTrail inner) : IAuditTrail
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => inner.AppendAsync(record, ct);

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (query.AuditId is not null)
            {
                if (Interlocked.Increment(ref _arrived) == 2) _both.TrySetResult();
                // A serialized drain never brings the second pass here while the first waits, so this times out.
                await Task.WhenAny(_both.Task, Task.Delay(TimeSpan.FromSeconds(1), ct));
            }

            await foreach (var record in inner.QueryAsync(query, ct))
                yield return record;
        }
    }

    private sealed class FailingCapturedTrail : ICapturedAuditTrail
    {
        public ValueTask AppendCapturedAsync(AuditRecord captured, CancellationToken ct = default) =>
            throw new InvalidOperationException("ck-6 injected trail outage");
    }
}
