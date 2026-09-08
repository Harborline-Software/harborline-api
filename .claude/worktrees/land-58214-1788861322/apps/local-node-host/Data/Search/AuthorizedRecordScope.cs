using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// The result of the fail-closed clip — the set of record ids a principal is authorized to see within a
/// tenant (ADR 0135 KG-search amendment G-1). This type is the <b>SOLE producer</b> of every KG / FTS5
/// query's record-narrowing <c>WHERE</c>; it is produced ONLY by
/// <see cref="IAuthorizedRecordSetProjection.ResolveAsync"/> over the shipped authorization closure, and the read
/// service accepts ONLY an instance of this type — there is no overload that runs a scan against a bare
/// id-list or no list at all. Together with <c>SearchClipArchFence</c> — which fences BOTH the FTS5
/// <c>MATCH</c> path AND the underlying <c>search_nodes</c> content table so neither can be read un-clipped
/// — that is what makes an un-clipped query <b>structurally impossible</b> rather than merely discouraged
/// (the no-mock-crypto fail-closed pattern; bug-1312 / earlier repository ticket #1325 forward).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three shapes, all fail-closed by construction.</b>
/// <list type="bullet">
///   <item><see cref="WholeTenant"/> — at least one active, cache-resident, records-read-covering grant
///     <c>Reaches</c> the whole tenant; every node in the tenant is authorized. The query's narrowing is then
///     "all rows in this tenant" (still tenant-scoped — never cross-tenant).</item>
///   <item><b>An explicit id set</b> — the union of the <c>RecordIds</c> of the principal's active,
///     cache-resident, records-read-covering <c>ForRecords</c> grants. The query narrows to
///     <c>record_id IN (…)</c>.</item>
///   <item><see cref="Empty"/> — no active grant reaches anything (no grants, all revoked/expired, or all
///     online-only/read-uncovered). The id set is empty, so the query returns NOTHING. This is the fail-closed
///     default: a principal with no live read-covering grant sees zero rows, never a tenant-wide fall-through.</item>
/// </list>
/// </para>
/// <para>
/// There is deliberately no public constructor and no "all rows, no clip" factory. The only ways to obtain
/// an instance are <see cref="ForRecordIds"/>, <see cref="EntireTenant"/>, and <see cref="Empty"/>, all of
/// which carry an explicit, fail-closed meaning. A caller cannot fabricate an "unclipped" scope.
/// </para>
/// </remarks>
public sealed class AuthorizedRecordScope
{
    private readonly HashSet<string>? _recordIds;

    private AuthorizedRecordScope(bool wholeTenant, HashSet<string>? recordIds)
    {
        WholeTenant = wholeTenant;
        _recordIds = recordIds;
    }

    /// <summary>
    /// True when the principal holds an active, cache-resident, records-read-covering grant reaching the WHOLE
    /// tenant — every node in the tenant is authorized (still tenant-scoped: never cross-tenant).
    /// </summary>
    public bool WholeTenant { get; }

    /// <summary>
    /// The explicit authorized record ids (defined only when NOT <see cref="WholeTenant"/>). Empty means the
    /// fail-closed default — the query returns nothing.
    /// </summary>
    public IReadOnlyCollection<string> RecordIds =>
        _recordIds is null ? System.Array.Empty<string>() : _recordIds;

    /// <summary>
    /// True when this scope authorizes NOTHING — not whole-tenant and an empty id set. The read service
    /// short-circuits to an empty result for this scope WITHOUT running any FTS5 / graph query (defence in
    /// depth: even a query bug cannot leak when there is nothing authorized).
    /// </summary>
    public bool IsEmpty => !WholeTenant && (_recordIds is null || _recordIds.Count == 0);

    /// <summary>The fail-closed empty scope — authorizes no record. The read service returns nothing for it.</summary>
    public static AuthorizedRecordScope Empty { get; } = new(wholeTenant: false, recordIds: null);

    /// <summary>The whole-tenant scope — every node in the tenant is authorized (tenant-scoped, never cross-tenant).</summary>
    public static AuthorizedRecordScope EntireTenant { get; } = new(wholeTenant: true, recordIds: null);

    /// <summary>
    /// A scope narrowed to an explicit set of authorized record ids. An empty input collapses to
    /// <see cref="Empty"/> (fail-closed). Duplicates are de-duplicated.
    /// </summary>
    public static AuthorizedRecordScope ForRecordIds(IEnumerable<string> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        var set = new HashSet<string>(recordIds, System.StringComparer.Ordinal);
        return set.Count == 0 ? Empty : new AuthorizedRecordScope(wholeTenant: false, set);
    }

    /// <summary>
    /// True when <paramref name="recordId"/> is authorized under this scope. Whole-tenant reaches every
    /// record; otherwise it must be in the explicit id set; an empty scope reaches nothing. Used by the
    /// clipped graph-expansion hop so a walk never crosses to an unauthorized record (G-1).
    /// </summary>
    public bool Authorizes(string recordId)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        return WholeTenant || (_recordIds is not null && _recordIds.Contains(recordId));
    }
}
