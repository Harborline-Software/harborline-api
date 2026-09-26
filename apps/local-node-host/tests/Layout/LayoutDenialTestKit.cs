using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
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
            .Resolve(Surface(), sources, LayoutBindingScope.Root(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)), trace, request);

    /// <summary>An owner card over a related record, then a field on the surface's own record.</summary>
    internal static LayoutDefinition Surface() => new(
        new("invoice", "1.0.0", Tenant.Value, LayoutCascadeLayer.TenantConfiguration,
            JsonSerializer.SerializeToElement(new { source = "t-731" }), "standard", false, []),
        1, LayoutMedium.Screen, LayoutIntent.Observe,
        [
            new LayoutBlock("owner-card", "layout.list", new LayoutStaticBinding(JsonDocument.Parse("\"Owner\"").RootElement.Clone()), [
                new LayoutBlock("owner-name", "layout.list", new LayoutRecordFieldBinding("name"), []),
            ], RelatedRelationship: "invoice.owner"),
            new LayoutBlock("supplier", "layout.list", new LayoutRecordFieldBinding("supplier"), []),
        ],
        [], [], [], null, []);

    internal static async Task<List<AuditRecord>> RowsAsync(IAuditTrail trail)
    {
        var rows = new List<AuditRecord>();
        await foreach (var row in trail.QueryAsync(new AuditQuery(Tenant))) rows.Add(row);
        return rows;
    }

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers every field from a fixed record and every related traversal with one outcome.</summary>
    internal sealed class OutcomeSources(LayoutRelatedResult outcome) : ILayoutBindingSources
    {
        public bool TryResolveField(LayoutBindingScope scope, string fieldPath, out JsonNode? value)
        {
            value = JsonValue.Create($"{fieldPath}-value");
            return true;
        }

        public bool TryResolveQuery(LayoutBindingScope scope, string viewDefinitionId, out JsonNode? value) => None(out value);
        public bool TryResolveMeasure(LayoutBindingScope scope, string measurePath, out JsonNode? value) => None(out value);
        public bool TryResolveTemplate(LayoutBindingScope scope, string templateDefinitionId, out JsonNode? value) => None(out value);

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
}
