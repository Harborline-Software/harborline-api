using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
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
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
using Harborline.Foundation.Definitions;
using Harborline.Foundation.RuleEngine;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>Shared T-731 fixtures: the surface, the sources, a migrated local-node.db and the denial pipeline.</summary>
internal static class LayoutDenialTestKit
{
    internal static readonly TenantId Tenant = new("tenant-731");
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
    internal static readonly LayoutRecordReference Owner = new("record-type.party", "party-19");

    internal static LayoutBindingResolution Resolve(ILayoutBindingSources sources, ILayoutDecisionTrace trace, LayoutResolutionRequest request)
        => new LayoutBindingResolver(new GuardEvaluator(new FixedTime(At)))
            .Resolve(Surface(), sources, LayoutBindingScope.Root(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)), trace, request,
                AllowLayoutAccess.Instance);

    private sealed class AllowLayoutAccess : ILayoutAccess
    {
        internal static readonly AllowLayoutAccess Instance = new();
        public bool CanAuthor() => true;
        public bool CanPublish() => true;
        public bool CanRead(LayoutBinding binding) => true;
        public bool CanOpen(string surfaceId) => true;
    }

    /// <summary>An owner card over a related record, then a field on the surface's own record.</summary>
    internal static LayoutDefinition Surface() => new(
        new("invoice", "1.0.0", Tenant.Value, LayoutCascadeLayer.TenantConfiguration,
            JsonSerializer.SerializeToElement(new { source = "t-731" }), "standard", false, [], new DefinitionContractVersion(1, 0)),
        1, LayoutMedium.Screen, LayoutIntent.Observe,
        [
            new LayoutBlock("owner-card", "layout.list", new LayoutStaticBinding(JsonDocument.Parse("\"Owner\"").RootElement.Clone()), [
                new LayoutBlock("owner-name", "layout.list", new LayoutRecordFieldBinding("name"), []),
            ], RelatedRelationship: "invoice.owner"),
            new LayoutBlock("supplier", "layout.list", new LayoutRecordFieldBinding("supplier"), []),
        ],
        [], [], [], null, []);

    internal static Task<List<AuditRecord>> RowsAsync(IAuditTrail trail) => RowsAsync(trail, Tenant);

    internal static async Task<List<AuditRecord>> RowsAsync(IAuditTrail trail, TenantId tenant)
    {
        var rows = new List<AuditRecord>();
        await foreach (var row in trail.QueryAsync(new AuditQuery(tenant))) rows.Add(row);
        return rows;
    }

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers every field from a fixed record and every related traversal with one outcome.</summary>
    internal sealed class OutcomeSources(LayoutRelatedResult outcome) : ILayoutBindingSources
    {
        public LayoutFieldResult ResolveField(LayoutBindingScope scope, string fieldPath)
            => LayoutFieldResult.Resolved(JsonValue.Create($"{fieldPath}-value"));

        public bool TryResolveQuery(LayoutBindingScope scope, string viewDefinitionId, out JsonNode? value) => None(out value);
        public bool TryResolveMeasure(LayoutBindingScope scope, string measurePath, out JsonNode? value) => None(out value);
        public bool TryResolveTemplate(LayoutBindingScope scope, string templateDefinitionId, string templateVersion, out JsonNode? value) => None(out value);

        public bool TryResolveCollection(LayoutBindingScope scope, string name, out IReadOnlyList<JsonNode?> rows)
        {
            rows = [];
            return false;
        }

        public LayoutRelatedResult ResolveRelated(LayoutBindingScope scope, string relationship) => outcome;

        private static bool None(out JsonNode? value)
        {
            value = null;
            return false;
        }
    }

    /// <summary>A gate log that refuses appends while <see cref="Down"/> is set.</summary>
    internal sealed class SwitchableTrail : IAuditTrail
    {
        private readonly InMemoryAuditTrail _inner = new();

        public bool Down { get; set; }

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
            => Down ? throw new InvalidOperationException("the gate log is down") : _inner.AppendAsync(record, ct);

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => _inner.QueryAsync(query, ct);
    }

    /// <summary>
    /// A local-node database migrated through the committed migrations, held in a shared in-memory SQLite
    /// database for the life of the fixture, so a disk's fsync stalls do not dominate the timing tests.
    /// <see cref="Fault"/> makes every context creation throw; <see cref="Delay"/> slows it.
    /// </summary>
    /// <remarks>
    /// Owner ruling Q39 option 3 (research R-0118): the slowdown is CPU work on the calling thread, measured
    /// by the monotonic clock, not a timer sleep. A timer wake-up can be late by an amount the OS chooses
    /// (macOS coalesces timers), which made the denied path's extra store work vary on mac16. With CPU work,
    /// timer lateness can only touch the host's own final deadline wait, which is the thing under test.
    /// </remarks>
    internal sealed class LocalNodeDb : IDbContextFactory<LocalNodeDbContext>, IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly DbContextOptions<LocalNodeDbContext> _options;

        public LocalNodeDb()
        {
            var source = $"Data Source=file:t731-{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(source);
            _keepAlive.Open();
            _options = new DbContextOptionsBuilder<LocalNodeDbContext>().UseSqlite(source).Options;
            using var db = CreateDbContext();
            db.Database.Migrate();
        }

        public bool Fault { get; set; }

        public TimeSpan Delay { get; set; }

        public LocalNodeDbContext CreateDbContext() => new(_options, LocalNodePatternAModuleCatalog.CreateModules());

        public Task<LocalNodeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero) Busy(Delay, cancellationToken);
            if (Fault) throw new InvalidOperationException("the local store is down");
            return Task.FromResult(CreateDbContext());
        }

        /// <summary>Spends <paramref name="cost"/> of CPU time on this thread, with no timer involved.</summary>
        private static void Busy(TimeSpan cost, CancellationToken cancellationToken)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < cost)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(64);
            }
        }

        public void Dispose() => _keepAlive.Dispose();
    }

    /// <summary>The denial pipeline over one local store and one gate log.</summary>
    internal sealed class Pipeline(IAuditTrail trail) : IDisposable
    {
        private readonly KeyPair _keys = KeyPair.Generate();

        public LocalNodeDb Db { get; } = new();
        public IAuditTrail Trail { get; } = trail;
        public LayoutDenialAlarms Alarms { get; } = new();
        public NodeEfLayoutDenialOutbox Outbox => _outbox ??= new(Db, NullLogger<NodeEfLayoutDenialOutbox>.Instance);
        private NodeEfLayoutDenialOutbox? _outbox;
        public LayoutDenialAppender Appender => _appender ??= new(Outbox, Trail, new Ed25519Signer(_keys), NullLogger.Instance);
        private LayoutDenialAppender? _appender;

        public LayoutDenialGateLog Trace() => new(Outbox, Appender, Alarms, Tenant, new FixedTime(At), NullLogger.Instance);

        public async Task<HealthCheckResult> HealthAsync()
            => await new LayoutDenialHealthCheck(Outbox, Alarms).CheckHealthAsync(new HealthCheckContext());

        public void Dispose()
        {
            Db.Dispose();
            _keys.Dispose();
        }
    }

    /// <summary>
    /// T-735: a signed fixture pack carrying Layout surfaces and the Records Record Types their related blocks
    /// traverse, exported, verified, installed and activated through the real pack engine. No released package
    /// carries a Layout surface yet, so the route tests install this one.
    /// </summary>
    /// <remarks>
    /// Oracles: the Record Type bodies are literals in the Records grammar, the same shape as
    /// <c>RecordsInstallEnforcementTests</c>' in-pack Dispatch and Route types; the relationship keys are the
    /// literal <c>{record_type_id}.{field_key}</c> spelling the T-735 owner ruling builds the map from.
    /// </remarks>
    internal static class SurfacePack
    {
        internal const string PackKey = "acme.finance";
        internal const string Version = "1.0.0";
        internal const string InvoiceType = "finance.invoice";
        internal const string PartyType = "finance.party";
        internal const string OwnerKey = "finance.invoice.owner";

        /// <summary>The surface with an owner card related through the single-target <c>owner</c> reference.</summary>
        internal const string Invoice = "invoice";

        /// <summary>A related block over <c>approvers</c>, a many-target reference.</summary>
        internal const string ApproversSurface = "invoice-approvers";

        /// <summary>A related block over <c>supplier</c>, a field that is not a reference.</summary>
        internal const string PlainFieldSurface = "invoice-plain-field";

        /// <summary>A related block over a field the Record Type does not declare.</summary>
        internal const string UnknownFieldSurface = "invoice-unknown-field";

        /// <summary>The owner relationship authored again inside the owner card, where the scope is not the root.</summary>
        internal const string NestedSurface = "invoice-nested";

        /// <summary>The owner relationship authored inside a repeating block, where the scope is one row.</summary>
        internal const string RepeatedRelatedSurface = "invoice-repeat-related";

        /// <summary>The host's field sources: a repeating collection, dotted paths, an undeclared field and a query.</summary>
        internal const string SourcesSurface = "invoice-sources";

        /// <summary>The owner card, in a pack whose closure holds the Record Type only through its dependency.</summary>
        internal const string DependencySurface = "invoice-from-dependency";

        /// <summary>The owner card, in a pack whose closure declares the root's Record Type twice.</summary>
        internal const string RedeclaredSurface = "invoice-redeclared";

        private const string Party =
            """{"class_id":"finance.parties","envelope":{"contract":{"major":1,"minor":0},"package_id":"acme.finance","section":"finance","tenant":"tenant-a"},"fields":[{"display_name":"Name","field_key":"name"}],"name":"Party","record_class":"master","record_type_id":"finance.party"}""";

        private const string InvoiceRecordType =
            """{"class_id":"finance.documents","envelope":{"contract":{"major":1,"minor":0},"package_id":"acme.finance","section":"finance","tenant":"tenant-a"},"fields":[{"display_name":"Supplier","field_key":"supplier"},{"display_name":"Owner","field_key":"owner","reference":{"cardinality":"one","on_delete":"block","parent":false,"target_type_id":"finance.party"}},{"display_name":"Approvers","field_key":"approvers","reference":{"cardinality":"many","on_delete":"block","parent":false,"target_type_id":"finance.party"}}],"name":"Invoice","record_class":"transactional","record_type_id":"finance.invoice"}""";

        /// <summary>Exports, installs and activates the pack and its consumer for <paramref name="tenant"/> into a fresh store.</summary>
        internal static async Task<InMemoryPackInstallStore> InstallAsync(TenantId tenant, bool exposeRecordTypes = true)
        {
            var store = new InMemoryPackInstallStore();
            await InstallOneAsync(store, tenant, PackKey,
                [
                    Records("finance.party", Party),
                    Records("finance.invoice", InvoiceRecordType),
                    Layout("finance.layout.invoice", Invoice, OwnerCard(OwnerKey, [Field("owner-name", "name")]), Field("supplier", "supplier")),
                    Layout("finance.layout.approvers", ApproversSurface, OwnerCard("finance.invoice.approvers", [Field("owner-name", "name")])),
                    Layout("finance.layout.plain", PlainFieldSurface, OwnerCard("finance.invoice.supplier", [Field("owner-name", "name")])),
                    Layout("finance.layout.unknown", UnknownFieldSurface, OwnerCard("finance.invoice.payee", [Field("owner-name", "name")])),
                    Layout("finance.layout.nested", NestedSurface,
                        OwnerCard(OwnerKey, [OwnerCard(OwnerKey, [Field("owner-name", "name")]) with { Id = "inner-card" }])),
                    Layout("finance.layout.repeat-related", RepeatedRelatedSurface,
                        Lines([OwnerCard(OwnerKey, [Field("owner-name", "name")])])),
                    Layout("finance.layout.sources", SourcesSurface,
                        Lines([Field("line-amount", "amount")]),
                        Field("customer-name", "customer.name"),
                        Field("customer-missing", "customer.nickname"),
                        Field("undeclared", "not_a_field"),
                        new LayoutBlock("view", "layout.table", new LayoutQueryBinding("finance.open-invoices"), [])),
                ], [], exposeRecordTypes
                    ? [new PackExposedDefinition("finance.invoice", "1.0.0", "sha256:" + new string('a', 64)),
                       new PackExposedDefinition("finance.party", "1.0.0", "sha256:" + new string('b', 64))]
                    : null);
            // The surface is in a consumer; the Record Type it binds through is only in the pack it depends on.
            await InstallOneAsync(store, tenant, "acme.surfaces",
                [Layout("surfaces.layout.invoice", DependencySurface, OwnerCard(OwnerKey, [Field("owner-name", "name")]), Field("supplier", "supplier"))],
                [new PackDependencyRef(PackKey, Version)]);
            return store;
        }

        /// <summary>A second Active pack carrying a surface identity <see cref="InstallAsync"/> already installed.</summary>
        internal static Task InstallDuplicateSurfaceAsync(InMemoryPackInstallStore store, TenantId tenant) => InstallOneAsync(store, tenant, "acme.duplicate",
            [Layout("duplicate.layout.invoice", Invoice, OwnerCard(OwnerKey, [Field("owner-name", "name")]))], []);

        /// <summary>A consumer whose own body redeclares the Record Type its dependency declares, so its closure declares it twice.</summary>
        internal static Task InstallRedeclaringPackAsync(InMemoryPackInstallStore store, TenantId tenant) => InstallOneAsync(store, tenant, "acme.redeclaring",
            [
                Records("redeclaring.party", Party.Replace("acme.finance", "acme.redeclaring", StringComparison.Ordinal)),
                Records("redeclaring.invoice", InvoiceRecordType.Replace("acme.finance", "acme.redeclaring", StringComparison.Ordinal)),
                Layout("redeclaring.layout.invoice", RedeclaredSurface, OwnerCard(OwnerKey, [Field("owner-name", "name")])),
            ],
            [new PackDependencyRef(PackKey, Version)]);

        private static async Task InstallOneAsync(InMemoryPackInstallStore store, TenantId tenant, string packKey,
            IReadOnlyList<PackContentSource> contents, IReadOnlyList<PackDependencyRef> dependencies,
            IReadOnlyList<PackExposedDefinition>? exposedDefinitions = null)
        {
            using var key = KeyPair.Generate();
            var codec = new PackFileCodec();
            var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec, timeProvider: TimeProvider.System);
            var request = new PackExportRequest(packKey, Version, "Acme finance", "T-735 Layout surface fixture",
                PackScopeTier.Horizontal, contents, dependencies, [], 1, Dcp: DomainComplianceProfile.General("test-author"),
                VerifiedProducers: store.ListInstalled(tenant),
                Exposes: exposedDefinitions?.Select(entry => entry.DefinitionKey).ToArray(),
                InterfaceVersion: exposedDefinitions is null ? null : 1,
                ExposedDefinitions: exposedDefinitions);
            var export = await exporter.ExportAsync(request, new Ed25519Signer(key));
            Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(error => $"{error.Code} {error.Target}")));

            var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), store,
                new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), Tests.Authorization.TestAuthorization.AllowGate(),
                layoutCapabilities: new Dictionary<string, string> { ["platform.layout"] = "1.0.0" })
            {
                RecordsAdmission = Packs.RecordsInstallClosureGateTests.Records().AdmitInstallAsync,
            };
            var context = new PackInstallContext(tenant,
                new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]),
                PackRevocationList.Empty, AdmittedInstant.FromRecordedAct(At), TimeSpan.FromDays(30), Principal: "test-operator");
            var outcome = await installer.InstallAsync(export.FileBytes!, context);
            Assert.True(outcome.Installed, string.Join("; ", outcome.RefusalCodes));
            store.Activate(tenant, packKey, Version);
        }

        /// <summary>An owner card: a stack container whose children resolve in the related record's scope.</summary>
        internal static LayoutBlock OwnerCard(string relationship, IReadOnlyList<LayoutBlock> children) => new(
            "owner-card", "layout.stack", new LayoutStaticBinding(JsonDocument.Parse("\"Owner\"").RootElement.Clone()), children,
            Container: new LayoutContainer(LayoutContainerKind.Stack), RelatedRelationship: relationship);

        /// <summary>A repeating block over the record's <c>lines</c>: its children resolve once per row.</summary>
        private static LayoutBlock Lines(IReadOnlyList<LayoutBlock> children) => new(
            "lines", "layout.list", new LayoutRecordFieldBinding("lines"), children,
            Container: new LayoutContainer(LayoutContainerKind.Stack), Repeating: true);

        private static LayoutBlock Field(string id, string field) => new(id, "layout.field", new LayoutRecordFieldBinding(field), []);

        private static PackContentSource Records(string key, string body)
            => new(key, PackContentKind.AssetTypeDefinition, Version, JsonNode.Parse(body)!);

        private static PackContentSource Layout(string key, string identity, params LayoutBlock[] blocks) => new(
            key, PackContentKind.Layout, Version, JsonNode.Parse(LayoutDefinitionJson.SerializeCanonical(new LayoutDefinition(
                new LayoutDefinitionEnvelope(identity, Version, "layout-tenant", LayoutCascadeLayer.TenantConfiguration,
                    JsonSerializer.SerializeToElement(new { source = "t-735" }), "standard", false,
                    [new LayoutDefinitionRequirement("platform.layout", "1.0.0")], new DefinitionContractVersion(1, 0)),
                1, LayoutMedium.Screen, LayoutIntent.Observe,
                blocks,
                [], [], [], null, [])))!);
    }

    /// <summary>Root records by id; the route reads one only after the caller may read it.</summary>
    internal sealed class FixtureRoots : ILayoutRootRecordSource
    {
        private readonly Dictionary<string, LayoutRootRecord> _records = new(StringComparer.Ordinal);

        public int Lookups { get; private set; }

        /// <summary>CPU time each lookup spends, with no timer involved.</summary>
        public TimeSpan Cost { get; set; }

        public void Add(string recordId, string recordTypeId, string valuesJson)
            => _records[recordId] = new LayoutRootRecord(recordTypeId,
                LayoutHostBindingSources.ValuesOf(JsonDocument.Parse(valuesJson).RootElement.Clone()));

        public ValueTask<LayoutRootRecord?> FindAsync(TenantId tenant, string recordId, CancellationToken ct = default)
        {
            Lookups++;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < Cost) Thread.SpinWait(64);
            return ValueTask.FromResult(_records.GetValueOrDefault(recordId));
        }
    }

    /// <summary>
    /// A Records instance store stand-in: the related records it holds, a switch that hides them all (a missing
    /// target), and a count of the lookups the route made.
    /// </summary>
    internal sealed class FixtureRelatedRecords : ILayoutRelatedRecordSource
    {
        private readonly Dictionary<LayoutRecordReference, IReadOnlyDictionary<string, JsonNode?>> _records = [];
        private int _lookups;

        public bool Present { get; set; } = true;

        public int Lookups => Volatile.Read(ref _lookups);

        public void Add(LayoutRecordReference target, string valuesJson)
            => _records[target] = LayoutHostBindingSources.ValuesOf(JsonDocument.Parse(valuesJson).RootElement.Clone());

        /// <summary>Lookups made from anywhere but the platform resolver's walk, that is before the host ran.</summary>
        public int OutsideResolver => Volatile.Read(ref _outside);

        private int _outside;

        public IReadOnlyDictionary<string, JsonNode?>? Find(TenantId tenant, LayoutRecordReference target)
        {
            Interlocked.Increment(ref _lookups);
            // The host's traversal callbacks run inside LayoutBindingResolver.Resolve, so a lookup made inside the
            // host's floor has it on the stack; a pre-load before the host does not.
            if (!new System.Diagnostics.StackTrace().GetFrames().Any(frame =>
                    frame.GetMethod()?.DeclaringType == typeof(LayoutBindingResolver)))
                Interlocked.Increment(ref _outside);
            return Present ? _records.GetValueOrDefault(target) : null;
        }
    }
}
