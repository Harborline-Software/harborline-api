using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
using Harborline.Foundation.RuleEngine;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>One related record a declared relationship reaches: its typed identity and its values.</summary>
public sealed record LayoutRelatedRecord(LayoutRecordReference Reference, LayoutBindingScope Scope);

/// <summary>
/// T-731, owner ruling 1 of 2026-09-24: the response floor every Layout resolution is padded to.
/// </summary>
public sealed class LayoutSurfaceOptions
{
    /// <summary>
    /// PLACEHOLDER, not a measurement: 250 ms holds until one replaces it. Derive the real value by
    /// resolving the largest released surface with every related binding denied, under representative
    /// load, taking the p99.9 of that time, doubling it, and rounding up to the timer tick (about
    /// 15.6 ms on Windows).
    /// </summary>
    public static readonly TimeSpan DefaultResponseFloor = TimeSpan.FromMilliseconds(250);

    /// <summary>The minimum time a resolution takes. A resolution that overruns it raises an alarm.</summary>
    public TimeSpan ResponseFloor { get; set; } = DefaultResponseFloor;

    /// <summary>
    /// Owner ruling of 2026-09-25, off by default: node-wide audit-degraded mode. While node audit health
    /// (<see cref="NodeEfLayoutDenialOutbox.IsAuditHealthyAsync"/>) is bad, every resolution of a surface
    /// that declares a related binding is refused with one <see cref="LayoutAuditDegradedException"/>,
    /// before any target is looked up, and it clears by itself once audit health returns. When off, a
    /// failed outbox write changes nothing the caller sees; it only raises the alert.
    /// </summary>
    public bool AuditDegradedMode { get; set; }
}

/// <summary>
/// The one refusal audit-degraded mode gives for every related-binding resolution, whatever the targets.
/// T-735 maps it to the route's response.
/// </summary>
public sealed class LayoutAuditDegradedException()
    : Exception("Related records are unavailable while this node's audit trail is degraded.")
{
    /// <summary>The stable code of the refusal.</summary>
    public const string Code = "layout.audit.degraded";
}

/// <summary>
/// DES-0052 layout-eng-31, host half (T-731): the host boundary a Layout surface resolves through. Each
/// related traversal is decided by the <see cref="AuthorizationGate"/> under the acting principal; a
/// denial goes only to <see cref="LayoutDenialGateLog"/>, and the viewer gets the resolution the platform
/// resolver produces for an absent target.
/// </summary>
/// <remarks>
/// <para>
/// Parity is proved in-process, against this class, not over HTTP: no api route, DI registration or
/// reader route exists until T-735 wires them.
/// </para>
/// <para>
/// Timing parity is by construction: every resolution completes no earlier than
/// <see cref="LayoutSurfaceOptions.ResponseFloor"/> after it starts, so the gate decision and the outbox
/// write that only a denial costs are hidden inside the floor. The gate-log append runs after the answer,
/// from the outbox. A resolution that finishes after the floor has leaked its path's timing; it is counted
/// on <see cref="LayoutDenialAlarms"/>, which <see cref="LayoutDenialHealthCheck"/> reports.
/// </para>
/// </remarks>
public sealed class LayoutSurfaceHost(
    AuthorizationGate gate, NodeEfLayoutDenialOutbox outbox, LayoutDenialAppender appender, LayoutDenialAlarms alarms,
    TimeProvider time, ILogger logger, LayoutSurfaceOptions options)
{
    /// <summary>
    /// Resolves <paramref name="definition"/> for <paramref name="principal"/> in <paramref name="requestId"/>.
    /// <paramref name="relationships"/> maps each declared relationship to its traversal, which returns
    /// <see langword="null"/> when the relationship has no target; an unmapped relationship is undeclared.
    /// </summary>
    public async Task<LayoutBindingResolution> ResolveAsync(
        LayoutDefinition definition,
        ILayoutBindingSources values,
        IReadOnlyDictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>> relationships,
        LayoutBindingScope root,
        TenantId tenant,
        ActorId principal,
        string requestId,
        CancellationToken ct = default)
    {
        var started = time.GetTimestamp();
        // Audit-degraded mode reads node audit health only: the decision is made before any target is
        // looked up, from the definition and the node, never from this request's lookups.
        if (options.AuditDegradedMode && DeclaresRelatedBinding(definition.Blocks)
            && !await outbox.IsAuditHealthyAsync(ct).ConfigureAwait(false))
        {
            await PadAsync(started, ct).ConfigureAwait(false);
            throw new LayoutAuditDegradedException();
        }

        var context = new AuthorizationWriteContext(principal, tenant, time.GetUtcNow());
        var resolver = new LayoutBindingResolver(new GuardEvaluator(time));
        var request = new LayoutResolutionRequest(requestId, principal.Value);
        var decisions = new Dictionary<string, AuthorizationDecision>(StringComparer.Ordinal);

        // The platform source is synchronous and the gate is not, so the related targets are decided
        // first: each discovery pass records the targets it has no decision for, the gate decides them,
        // and the next pass can walk into the related records the gate allowed. Discovery passes write
        // no evidence; only the final pass, with every decision in hand, runs against the gate log.
        while (true)
        {
            var undecided = new HashSet<string>(StringComparer.Ordinal);
            resolver.Resolve(definition, new AuthorizedRelatedSources(values, relationships, decisions, undecided),
                root, NoTrace.Instance, request, ct);
            if (undecided.Count == 0) break;
            foreach (var recordId in undecided)
                decisions[recordId] = await gate.DecideAsync(
                    context.Request(RecordsRead, AuthorizationGate.RecordKindFor(RecordsRead), recordId), ct).ConfigureAwait(false);
        }

        var trace = new LayoutDenialGateLog(outbox, appender, alarms, tenant, time, logger);
        var resolution = resolver.Resolve(definition,
            new AuthorizedRelatedSources(values, relationships, decisions, undecided: null), root, trace, request, ct);
        await trace.WrittenAsync().ConfigureAwait(false);

        await PadAsync(started, ct).ConfigureAwait(false);
        // The signed append runs from the outbox only once the floor has passed, so its work never lands
        // inside a denied resolution's own timing window. It never throws; a failure is retried by the drain.
        _ = trace.AppendAsync();
        return resolution;
    }

    private async Task PadAsync(long started, CancellationToken ct)
    {
        var remaining = options.ResponseFloor - time.GetElapsedTime(started);
        if (remaining < TimeSpan.Zero) alarms.FloorOverrun();
        // A coarse timer can wake early, so wait until the deadline has really passed: every path then ends
        // at the same deadline, never before it.
        while (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, time, ct).ConfigureAwait(false);
            remaining = options.ResponseFloor - time.GetElapsedTime(started);
        }
    }

    private static bool DeclaresRelatedBinding(IEnumerable<LayoutBlock>? blocks)
        => (blocks ?? []).Any(block => block.RelatedRelationship is { Length: > 0 } || DeclaresRelatedBinding(block.Children));

    private static readonly AuthorizationOperation RecordsRead = AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead);

    private sealed class NoTrace : ILayoutDecisionTrace
    {
        public static readonly NoTrace Instance = new();
        public void RecordDenial(LayoutRelatedDenial denial) { }
    }

    /// <summary>The host's values, with each related traversal answered from the gate's records:read decision.</summary>
    private sealed class AuthorizedRelatedSources(
        ILayoutBindingSources values,
        IReadOnlyDictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>> relationships,
        IReadOnlyDictionary<string, AuthorizationDecision> decisions,
        HashSet<string>? undecided) : ILayoutBindingSources
    {
        public bool TryResolveField(LayoutBindingScope scope, string fieldPath, out JsonNode? value) => values.TryResolveField(scope, fieldPath, out value);
        public bool TryResolveQuery(LayoutBindingScope scope, string viewDefinitionId, out JsonNode? value) => values.TryResolveQuery(scope, viewDefinitionId, out value);
        public bool TryResolveMeasure(LayoutBindingScope scope, string measurePath, out JsonNode? value) => values.TryResolveMeasure(scope, measurePath, out value);
        public bool TryResolveTemplate(LayoutBindingScope scope, string templateDefinitionId, out JsonNode? value) => values.TryResolveTemplate(scope, templateDefinitionId, out value);
        public bool TryResolveCollection(LayoutBindingScope scope, string name, out IReadOnlyList<JsonNode?> rows) => values.TryResolveCollection(scope, name, out rows);

        public LayoutRelatedResult ResolveRelated(LayoutBindingScope scope, string relationship)
        {
            if (!relationships.TryGetValue(relationship, out var traverse)) return LayoutRelatedResult.Undeclared;
            if (traverse(scope) is not { } related) return LayoutRelatedResult.Absent;
            if (!decisions.TryGetValue(related.Reference.RecordId, out var decision))
            {
                // A discovery pass: note the target and walk no further into it yet.
                (undecided ?? throw new InvalidOperationException(
                    $"Related target '{related.Reference.RecordId}' appeared only in the final pass.")).Add(related.Reference.RecordId);
                return LayoutRelatedResult.Absent;
            }
            return decision.Verdict == AuthorizationVerdict.Allowed
                ? LayoutRelatedResult.Resolved(related.Scope)
                : LayoutRelatedResult.Denied(
                    decision.Request.GrantRefusal ?? AuthorizationRefusalRenderer.PermissionRequiredCode,
                    decision.Request.Target.Scope.Value, related.Reference);
        }
    }
}
