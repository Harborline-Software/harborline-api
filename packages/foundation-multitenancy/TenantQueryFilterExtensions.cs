using System.Linq.Expressions;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.MultiTenancy;

/// <summary>
/// LINQ extension methods for applying the canonical per-tenant row filter to an
/// <see cref="IQueryable{T}"/> where <c>T</c> implements
/// <see cref="IMustHaveTenant"/>.
///
/// <para><b>When to use this</b></para>
/// <para>
/// EF Core's <c>HasQueryFilter</c> wiring (ADR 0092 Step 2.1+) handles the common
/// case automatically via the model-level global query filter. Use
/// <see cref="WhereTenant{T}(IQueryable{T}, ITenantContext)"/> and the
/// <see cref="WhereTenant{T}(IQueryable{T}, TenantId)"/> overload <em>only</em>
/// when the automatic filter does not apply:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>FromSqlRaw</c> / <c>FromSqlInterpolated</c> paths — EF does not apply
///     query filters to raw-SQL entry points.
///   </description></item>
///   <item><description>
///     Dynamic LINQ assemblies that bypass the DbSet and issue a fresh
///     <see cref="IQueryable{T}"/> without a backing DbContext.
///   </description></item>
///   <item><description>
///     Control-plane queries with explicit cross-tenant visibility intent — use
///     with an ADR 0091 composition-root comment explaining why.
///   </description></item>
///   <item><description>
///     Background jobs that thread <see cref="TenantId"/> through their payload
///     but do not carry a full <see cref="ITenantContext"/> scope.
///   </description></item>
/// </list>
///
/// <para><b>IgnoreQueryFilters is the last resort</b></para>
/// <para>
/// <c>IgnoreQueryFilters()</c> (EF Core) completely disables all global query
/// filters including the per-tenant one. Use it only for cross-tenant admin
/// queries and only when accompanied by a sec-eng attestation per the earlier repository
/// research PR 69 verdict. ADR 0092 Step 4a analyzer (future PR) will flag
/// unattested <c>IgnoreQueryFilters()</c> call sites.
/// </para>
///
/// <para><b>References</b></para>
/// <list type="bullet">
///   <item><description>ADR 0091 — Tenant resolution composition root</description></item>
///   <item><description>
///     ADR 0092 — Substrate tenant-keyed repository contract (Step 2.0)
///   </description></item>
///   <item><description>
///     Harborline research PR 69 — hybrid strategy spec:
///     HasQueryFilter (default) + WhereTenant (explicit opt-in) +
///     IgnoreQueryFilters (last resort, sec-eng attestation required)
///   </description></item>
/// </list>
/// </summary>
public static class TenantQueryFilterExtensions
{
    /// <summary>
    /// Applies the canonical per-tenant filter to <paramref name="query"/> using
    /// the ambient <see cref="ITenantContext"/>'s resolved tenant. Returns a
    /// filtered <see cref="IQueryable{T}"/> that only includes entities matching
    /// the caller's tenant.
    ///
    /// <para>
    /// Use this when EF Core's automatic <c>HasQueryFilter</c> wiring does NOT
    /// apply: <c>FromSqlRaw</c> paths, dynamic LINQ, control-plane queries with
    /// explicit cross-tenant visibility intent, stored procedures.
    /// </para>
    ///
    /// <para>
    /// Produces the same SQL predicate as an inline
    /// <c>.Where(e =&gt; e.TenantId == tenantContext.Tenant.Id)</c> — no
    /// additional plan overhead in the canonical case (ADR 0092 Step 2.0 /
    /// earlier repository research PR 69).
    /// </para>
    ///
    /// <para><b>Throws</b></para>
    /// <para>
    /// <see cref="InvalidOperationException"/> when
    /// <see cref="ITenantContext.Tenant"/> is <see langword="null"/>. This is a
    /// composition-root bug; it is never reachable in correct code per ADR 0091.
    /// </para>
    /// </summary>
    /// <typeparam name="T">
    /// Entity type that implements <see cref="IMustHaveTenant"/> — guarantees a
    /// non-nullable <see cref="ITenantScoped.TenantId"/> property.
    /// </typeparam>
    /// <param name="query">The source queryable to filter.</param>
    /// <param name="tenantContext">
    /// The ambient tenant context. Must have a resolved
    /// <see cref="ITenantContext.Tenant"/>.
    /// </param>
    /// <returns>A filtered queryable scoped to the resolved tenant.</returns>
    /// <exception cref="ArgumentNullException">
    /// When <paramref name="query"/> or <paramref name="tenantContext"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// When <see cref="ITenantContext.Tenant"/> is null (unresolved tenant;
    /// composition-root bug per ADR 0091).
    /// </exception>
    public static IQueryable<T> WhereTenant<T>(
        this IQueryable<T> query,
        ITenantContext tenantContext)
        where T : IMustHaveTenant
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (tenantContext.Tenant is null)
        {
            throw new InvalidOperationException(
                "ITenantContext has no resolved tenant. This indicates a composition-root " +
                "configuration error — ITenantContext must always be resolved before a " +
                "tenant-scoped query is executed. See ADR 0091.");
        }

        return WhereTenant(query, tenantContext.Tenant.Id);
    }

    /// <summary>
    /// Overload taking an explicit <see cref="TenantId"/>. Used by service layers
    /// that have the tenant identifier in scope but not the full
    /// <see cref="ITenantContext"/> — for example, background jobs that thread
    /// <see cref="TenantId"/> through their job payload.
    ///
    /// <para>
    /// Produces the same SQL predicate as an inline
    /// <c>.Where(e =&gt; e.TenantId == tenantId)</c>.
    /// </para>
    ///
    /// <para><b>References:</b> ADR 0092 Step 2.0; earlier repository research PR 69.</para>
    /// </summary>
    /// <typeparam name="T">
    /// Entity type that implements <see cref="IMustHaveTenant"/>.
    /// </typeparam>
    /// <param name="query">The source queryable to filter.</param>
    /// <param name="tenantId">The tenant identifier to match against.</param>
    /// <returns>A filtered queryable scoped to <paramref name="tenantId"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// When <paramref name="query"/> is null.
    /// </exception>
    public static IQueryable<T> WhereTenant<T>(
        this IQueryable<T> query,
        TenantId tenantId)
        where T : IMustHaveTenant
    {
        ArgumentNullException.ThrowIfNull(query);

        // sec-eng SPOT-CHECK amendment C1 (2026-05-21T14:10Z): reject sentinel
        // TenantId values at the call site so a default(TenantId), TenantId.System,
        // or any __-prefixed sentinel can never silently turn the query into
        // an "all-tenants" predicate. The substrate-canonical bypass mechanism
        // for cross-tenant reads is `IgnoreQueryFilters` (ADR 0092 §A4/§B4
        // attestation path), NOT WhereTenant with a sentinel.
        if (tenantId.IsSystemSentinel)
        {
            throw new ArgumentException(
                "WhereTenant rejects default / system / sentinel TenantId values. " +
                "Cross-tenant reads must go through the IgnoreQueryFilters attestation " +
                "path per ADR 0092 §A4/§B4 (sec-eng SPOT-CHECK amendment C1 on the sibling shipyard PR).",
                nameof(tenantId));
        }

        return query.Where(e => e.TenantId == tenantId);
    }
}
