using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
using Harborline.Foundation.RuleEngine;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-731 slice 1 — DES-0052 layout-eng-31 (host half) and layout-run-5: the host's
/// <see cref="ILayoutDecisionTrace"/> writes each related-binding denial into the authorization gate log
/// while the platform resolver is still running, not afterwards.
/// </summary>
public sealed class LayoutDenialGateLogTests
{
    internal static readonly TenantId Tenant = new("tenant-731");
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
    internal static readonly LayoutRecordReference Owner = new("record-type.party", "party-19");

    [Fact(DisplayName = "layout-eng-31, layout-run-5: a denied related binding writes exactly one gate-log record carrying every field, during resolution")]
    public async Task ADeniedRelatedBindingWritesExactlyOneGateLogRecordDuringResolution()
    {
        var trail = new InMemoryAuditTrail();
        var trace = new LayoutDenialGateLog(trail, new Ed25519Signer(KeyPair.Generate()), Tenant,
            new FixedTime(At), NullLogger.Instance);
        var countedDuringResolution = -1;
        var sources = new OutcomeSources(LayoutRelatedResult.Denied("authorization.permission_required", "/records/party-19", Owner),
            // The block after the related one resolves after the denial: the record must already be there.
            onField: () => countedDuringResolution = Count(trail));

        Resolve(sources, trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));

        Assert.Equal(1, countedDuringResolution);
        var record = Assert.Single(await RowsAsync(trail));
        Assert.Equal(LayoutDenialGateLog.LayoutRelatedDeniedEventType, record.EventType);
        Assert.Equal(At, record.OccurredAt);
        Assert.Equal("principal.clerk-4", record.Actor?.Value);
        Assert.Equal(("record", "party-19"), (record.Target?.RecordKind, record.Target?.RecordId));
        Assert.Equal("records:read", record.Act?.Operation.Value);
        Assert.Equal(
            new LayoutRelatedDenial("request-7", "principal.clerk-4", "owner-card", LayoutBindingKinds.Static,
                "invoice.owner", Owner, "authorization.permission_required", "/records/party-19"),
            LayoutDenialGateLog.Denial(record));
    }

    [Fact(DisplayName = "layout-eng-31: a missing related target writes nothing to the gate log")]
    public async Task AMissingRelatedTargetWritesNothing()
    {
        var trail = new InMemoryAuditTrail();
        var trace = new LayoutDenialGateLog(trail, new Ed25519Signer(KeyPair.Generate()), Tenant,
            new FixedTime(At), NullLogger.Instance);

        Resolve(new OutcomeSources(LayoutRelatedResult.Absent), trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));

        Assert.Empty(await RowsAsync(trail));
    }

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

    private static int Count(IAuditTrail trail) => RowsAsync(trail).GetAwaiter().GetResult().Count;

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers every field from a fixed record and every related traversal with one outcome.</summary>
    internal sealed class OutcomeSources(LayoutRelatedResult outcome, Action? onField = null) : ILayoutBindingSources
    {
        public bool TryResolveField(LayoutBindingScope scope, string fieldPath, out JsonNode? value)
        {
            onField?.Invoke();
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
}
