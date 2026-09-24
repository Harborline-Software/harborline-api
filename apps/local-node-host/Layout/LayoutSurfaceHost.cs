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
        var sources = new AuthorizedRelatedSources(values, relationships, gate,
            new AuthorizationWriteContext(principal, tenant, time.GetUtcNow()), ct);
        var resolution = new LayoutBindingResolver(new GuardEvaluator(time)).Resolve(
            definition, sources, root, new LayoutDenialGateLog(trail, signer, tenant, time, logger),
            new LayoutResolutionRequest(requestId, principal.Value), ct);
        var remaining = responseFloor - time.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, time, ct).ConfigureAwait(false);
        return resolution;
    }

    /// <summary>The host's values, with each related traversal decided by the gate as records:read.</summary>
    private sealed class AuthorizedRelatedSources(
        ILayoutBindingSources values,
        IReadOnlyDictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>> relationships,
        AuthorizationGate gate,
        AuthorizationWriteContext context,
        CancellationToken ct) : ILayoutBindingSources
    {
        private static readonly AuthorizationOperation RecordsRead = AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead);

        public bool TryResolveField(LayoutBindingScope scope, string fieldPath, out JsonNode? value) => values.TryResolveField(scope, fieldPath, out value);
        public bool TryResolveQuery(LayoutBindingScope scope, string viewDefinitionId, out JsonNode? value) => values.TryResolveQuery(scope, viewDefinitionId, out value);
        public bool TryResolveMeasure(LayoutBindingScope scope, string measurePath, out JsonNode? value) => values.TryResolveMeasure(scope, measurePath, out value);
        public bool TryResolveTemplate(LayoutBindingScope scope, string templateDefinitionId, out JsonNode? value) => values.TryResolveTemplate(scope, templateDefinitionId, out value);
        public bool TryResolveCollection(LayoutBindingScope scope, string name, out IReadOnlyList<JsonNode?> rows) => values.TryResolveCollection(scope, name, out rows);

        public LayoutRelatedResult ResolveRelated(LayoutBindingScope scope, string relationship)
        {
            if (!relationships.TryGetValue(relationship, out var traverse)) return LayoutRelatedResult.Undeclared;
            if (traverse(scope) is not { } related) return LayoutRelatedResult.Absent;
            var request = context.Request(RecordsRead, AuthorizationGate.RecordKindFor(RecordsRead), related.Reference.RecordId);
            // ponytail: the platform source is synchronous; the gate's async decision blocks this thread.
            var decision = gate.DecideAsync(request, ct).AsTask().GetAwaiter().GetResult();
            return decision.Verdict == AuthorizationVerdict.Allowed
                ? LayoutRelatedResult.Resolved(related.Scope)
                : LayoutRelatedResult.Denied(
                    decision.Request.GrantRefusal ?? AuthorizationRefusalRenderer.PermissionRequiredCode,
                    request.Target.Scope.Value, related.Reference);
        }
    }
}
