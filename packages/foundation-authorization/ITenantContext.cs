namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Transitional sum-interface facade. Resolves the current tenant and caller
/// identity. Scoped per request. Accelerators / apps register an
/// implementation (e.g. claims-based) in DI via
/// <see cref="DependencyInjection.TenantContextServiceCollectionExtensions.AddHarborlineTenantContext{TConcrete}(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Revised by ADR 0091 Step 1 (Revision 2; amendment 1).</b> Previously a
/// 4-member interface conflating tenant identity, caller identity, and
/// authorization. Now a sum-interface extending three single-responsibility
/// interfaces:
/// <list type="bullet">
///   <item><see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/> — tenant resolution (canonical surface; ADR 0031 control-plane intent).</item>
///   <item><see cref="ICurrentUser"/> — caller identity (OIDC seam).</item>
///   <item><see cref="IAuthorizationContext"/> — policy evaluation.</item>
/// </list>
/// The default-implemented <see cref="TenantId"/> string property preserves
/// source compatibility for the 14 legacy consumers; new code MUST inject the
/// narrowed interface it actually needs rather than this facade.
/// </para>
/// <para>
/// <b>Migration timeline (ADR 0091 R2 §"Compatibility plan"):</b>
/// <list type="number">
///   <item>Step 1 (this PR) — facade introduction; zero breakage.</item>
///   <item>Step 2.0 — dedicated <c>SignalBridgeDbContext</c> rewrite PR (mandatory sec-eng council).</item>
///   <item>Step 2.1+ — batched endpoint migrations (3–4 PRs; each a breaking API change taking the full review path; NOT <c>pattern-009</c>-eligible).</item>
///   <item>Step 3 — test fixture migration.</item>
///   <item>Step 4 — mark this facade <c>[Obsolete]</c>; ship the <c>RequestContextMixingAnalyzer</c> (amendment A2).</item>
///   <item>Step 5 — delete this facade after one-cohort grace.</item>
/// </list>
/// </para>
/// <para>
/// TODO (Step 4 — ADR 0091 R2 amendment A2): ship
/// <c>Harborline.Api.Foundation.Authorization.Analyzers.RequestContextMixingAnalyzer</c>
/// in the same PR that marks this interface <c>[Obsolete]</c>. The analyzer
/// fails closed when a request pipeline binds both
/// <c>IBrowserTenantContext</c> (data-plane; subdomain-resolved) and any
/// control-plane facade-or-narrowed interface to the same endpoint.
/// </para>
/// </remarks>
public interface ITenantContext
    : Harborline.Api.Foundation.MultiTenancy.ITenantContext,
      ICurrentUser,
      IAuthorizationContext
{
    /// <summary>
    /// Legacy string tenant id. Default-implemented for source compatibility
    /// — delegates to <c>Tenant?.Id.ToString() ?? string.Empty</c>. Returns
    /// the empty string when <see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext.Tenant"/>
    /// is null (i.e., unresolved). New code MUST inject
    /// <see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/> directly
    /// rather than this facade and read <c>Tenant?.Id</c> as the typed
    /// <see cref="Harborline.Foundation.Assets.Common.TenantId"/>.
    /// </summary>
    string TenantId => Tenant?.Id.ToString() ?? string.Empty;
}
