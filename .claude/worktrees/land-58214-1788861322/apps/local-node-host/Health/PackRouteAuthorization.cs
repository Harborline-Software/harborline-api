using Microsoft.AspNetCore.Http;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>The two typed pack operations the family resolves, parsed once from the platform vocabulary
/// rather than re-parsed per request (and never a bare string at the gate).</summary>
internal static class PackOperation
{
    /// <summary>The OPERATE-side operation (<c>packages:operate</c>) — preview / install / activate /
    /// deactivate / list / channels.</summary>
    internal static AuthorizationOperation Operate { get; } = AuthorizationOperation.Parse(Permission.PackagesOperate);

    /// <summary>The AUTHOR-side operation (<c>packages:author</c>) — compose / affirm / export / verify.</summary>
    internal static AuthorizationOperation Author { get; } = AuthorizationOperation.Parse(Permission.PackagesAuthor);
}

/// <summary>
/// The point-of-use current-authorization resolution for the pack and feed route family (ticket 205,
/// ledger L592/L600/L671).
/// </summary>
/// <remarks>
/// <para>
/// Every route in the family used to ask an ambient <c>IAuthorizationContext.HasPermission(string)</c>,
/// which carries neither the record the act touches nor the instant it happens. Each route now builds its
/// authority at the point of use — the acting party from THIS request, the tenant from the active team, the
/// instant from the clock — and resolves one <see cref="AuthorizationGate"/> decision for the act.
/// </para>
/// <para>
/// <b>The record target.</b> A route that addresses one named pack passes it as the <c>pack</c> record
/// target, so a grant scoped to a different pack refuses. A route that addresses the INSTALL rather than
/// one pack (the channel table, a channel check, the installed-pack list, and the two no-effect
/// verify/preview surfaces over an artifact that is not yet a record) passes no record target: the gate
/// admits that shape only for an operation the definition side declares install-wide
/// (<see cref="PermissionVocabulary.InstallWideOperations"/>), never because a call site said so.
/// </para>
/// </remarks>
internal static class PackRouteAuthorization
{
    /// <summary>The record kind a pack act targets — the kind <c>AuthorizationGate.Validate</c> accepts for
    /// the <c>packages</c> resource.</summary>
    internal const string PackRecordKind = "pack";

    /// <summary>The server-derived authority for THIS request: the acting party, the tenant, and the
    /// instant the act happens. Never an ambient read.</summary>
    internal static AuthorizationWriteContext Authority(HttpContext http, TenantId tenant, TimeProvider time) =>
        RequestAuthorization.Authority(http, tenant, time);

    /// <summary>
    /// Resolves <paramref name="operation"/> for <paramref name="packKey"/> through the gate and returns the
    /// fail-closed refusal when the single decision denies, or <see langword="null"/> when the act may
    /// proceed. A blank <paramref name="packKey"/> means the route addresses no one pack and the request is
    /// built install-wide; the gate refuses that shape unless the operation is declared install-wide.
    /// </summary>
    internal static async ValueTask<IResult?> RefusalAsync(
        AuthorizationGate gate,
        AuthorizationWriteContext authority,
        AuthorizationOperation operation,
        string? packKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);

        // A pack key is caller-supplied on the activate/deactivate/compose bodies. Canonicalising it runs
        // through the same ScopeExpression the gate itself parses (AuthorizationGate.CanonicalTargetScope),
        // so any key that cannot become a record id — a scope separator, or a '.'/'..' segment — is refused
        // fail-closed here rather than thrown out of the gate as a 500. Not a bespoke character list: the
        // parser is the single source of truth for what a record id may look like.
        AuthorizationGateRequest request;
        try
        {
            request = string.IsNullOrWhiteSpace(packKey)
                ? authority.InstallWide(operation)
                : authority.Request(operation, PackRecordKind, packKey);
        }
        catch (ArgumentException)
        {
            return PackRouteAuthz.Denied(operation.Value);
        }

        var decision = await gate.DecideAsync(request, ct).ConfigureAwait(false);
        return decision.Verdict == AuthorizationVerdict.Allowed ? null : PackRouteAuthz.Denied(operation.Value);
    }
}
