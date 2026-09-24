using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
using Harborline.Foundation.RuleEngine;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>One related record a declared relationship reaches: its typed identity and its values.</summary>
public sealed record LayoutRelatedRecord(LayoutRecordReference Reference, LayoutBindingScope Scope);

/// <summary>
/// DES-0052 layout-eng-31, host half (T-731): the host boundary a Layout surface resolves through. Each
/// related traversal is decided by the <see cref="AuthorizationGate"/> under the acting principal; a
/// denial goes only to <see cref="LayoutDenialGateLog"/>, and the viewer gets the resolution the platform
/// resolver produces for an absent target.
/// </summary>
/// <remarks>
/// Timing parity is by construction: every resolution completes no earlier than
/// <paramref name="responseFloor"/> after it starts, so the gate decision and the gate-log append that
/// only a denial costs are hidden inside the floor. The floor must exceed the denied path's worst
/// case; past it, the difference shows again.
/// </remarks>
public sealed class LayoutSurfaceHost(
    AuthorizationGate gate, IAuditTrail trail, IOperationSigner signer, TimeProvider time, ILogger logger,
    TimeSpan responseFloor)
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

        var trace = new LayoutDenialGateLog(trail, signer, tenant, time, logger);
        var resolution = resolver.Resolve(definition,
            new AuthorizedRelatedSources(values, relationships, decisions, undecided: null), root, trace, request, ct);
        await trace.WrittenAsync().ConfigureAwait(false);

        var remaining = responseFloor - time.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, time, ct).ConfigureAwait(false);
        return resolution;
    }

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
