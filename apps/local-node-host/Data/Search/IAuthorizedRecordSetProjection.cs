using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// The fail-closed clip seam (ADR 0135 KG-search amendment G-1) — projects the shipped grant store into the
/// set-valued <see cref="AuthorizedRecordScope"/> a principal is authorized to see within a tenant. This is
/// the <b>SOLE producer</b> of the KG / FTS5 query's record-narrowing <c>WHERE</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this seam (the no-mock-crypto fail-closed pattern).</b> The bare ADR 0077
/// <c>IPermissionResolver</c> / the role binding source authorize a role <i>tenant-wide</i> — a
/// record-scoped grant does not narrow there (only the live closure bites, and only when a
/// resource is named). So a KG query that consulted the bare resolver would read tenant-wide rows BY
/// DEFAULT (the gate confirmed this on <c>origin/main</c>). This projection is the only thing standing
/// between a correct query and a full-tenant leak. It consumes the grant store's set-valued residency-aware
/// scope directly and NEVER falls through to the bare resolver's point-check.
/// </para>
/// <para>
/// <b>G-2 — one fenced snapshot.</b> The projection reconciles and reads the materialised closure on the
/// supplied connection and transaction before the index scan. A source mutation therefore lands wholly
/// before or after closure resolution plus search; an inactive grant reaches nothing.
/// </para>
/// <para>
/// <b>G-3 — residency.</b> Only <c>GrantResidency.Cache</c> grants contribute to the scope. An
/// <c>OnlineOnly</c> grant authorizes nothing here, and (the stronger half of G-3) its records are never
/// indexed in the first place.
/// </para>
/// </remarks>
public interface IAuthorizedRecordSetProjection
{
    /// <summary>
    /// Resolves the fail-closed authorized-record scope for <paramref name="principalId"/> within
    /// <paramref name="tenantId"/> at <paramref name="at"/>. Returns <see cref="AuthorizedRecordScope.Empty"/>
    /// (authorizes nothing) when the principal holds no active, cache-resident grant — never a tenant-wide
    /// fall-through.
    /// </summary>
    /// <param name="connection">The caller's already-open SQLite connection.</param>
    /// <param name="transaction">The caller's BEGIN IMMEDIATE transaction.</param>
    /// <param name="tenantId">The tenant whose closure is consulted (the per-tenant isolation boundary).</param>
    /// <param name="principalId">The acting principal the scope is computed for.</param>
    /// <param name="at">The instant the grants' active/expiry window is evaluated at (G-2 same-snapshot).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthorizedRecordScope> ResolveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TenantId tenantId,
        ActorId principalId,
        System.DateTimeOffset at,
        CancellationToken ct = default);
}
