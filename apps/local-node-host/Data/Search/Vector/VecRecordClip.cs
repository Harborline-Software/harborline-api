using System;
using System.Collections.Generic;
using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The SOLE producer of the vector query's record-narrowing <c>WHERE</c> (ADR 0135 KG-search F3-lift amendment,
/// Slice 1b; G-1). Builds the EXACT <c>record_id</c> pre-filter — a metadata-column <c>record_id IN (…)</c>
/// (NOT a partition key — the spike-proven ~1× storage design) — from a fail-closed
/// <see cref="AuthorizedRecordScope"/>, parameterized (never interpolated).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is centralized.</b> Both KNN engines (the native <c>vec0</c> path and the brute-force path) build
/// their record pre-filter HERE, from the scope, so there is exactly one place a vec record-narrowing predicate
/// is produced — the same discipline that makes the FTS path's <c>BuildRecordClip</c> the sole producer of its
/// <c>WHERE</c>. An arch-fence asserts no other vec source issues a <c>vec0 MATCH</c> / reads
/// <c>search_vec_rows</c> without this clip, so an un-clipped vector read is structurally impossible.
/// </para>
/// <para>
/// <b>Pre-filter, not post-filter (the exactness property).</b> The returned fragment is ANDed into the scan's
/// <c>WHERE</c>, so on the brute-force path it narrows the rows LOADED before distance, and on the <c>vec0</c>
/// path it is the metadata constraint <c>sqlite-vec</c> applies as a true pre-filter (spike R-1: returns k from
/// the allowed set even when the global-nearest are forbidden). Whole-tenant collapses to a tautology, leaving
/// the caller's <c>tenant_id</c> predicate as the only narrowing (never cross-tenant). An empty scope never
/// reaches here — callers short-circuit it.
/// </para>
/// </remarks>
public static class VecRecordClip
{
    /// <summary>
    /// Builds the <c>record_id</c> clip fragment + its parameter binder from <paramref name="scope"/>.
    /// </summary>
    /// <param name="scope">The fail-closed authorized-record scope (must not be empty — callers short-circuit empty).</param>
    /// <param name="column">The qualified <c>record_id</c> column reference (defaults to a bare <c>record_id</c>).</param>
    public static (string Sql, Action<SqliteCommand> Binder) Build(
        AuthorizedRecordScope scope, string column = "record_id")
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.WholeTenant)
        {
            // No per-record narrowing beyond the tenant predicate the caller already applied.
            return ("1 = 1", static _ => { });
        }

        var ids = new List<string>(scope.RecordIds);
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            names[i] = "$vr" + i.ToString(CultureInfo.InvariantCulture);
        }

        var sql = $"{column} IN ({string.Join(", ", names)})";
        void Binder(SqliteCommand cmd)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                cmd.Parameters.AddWithValue(names[i], ids[i]);
            }
        }

        return (sql, Binder);
    }

    /// <summary>Builds a parameterized candidate-id intersection for an already-clipped query.</summary>
    internal static (string Sql, Action<SqliteCommand> Binder) BuildCandidateIntersection(
        IReadOnlyList<string> recordIds)
    {
        var names = new string[recordIds.Count];
        for (var i = 0; i < recordIds.Count; i++)
        {
            names[i] = "$candidate" + i.ToString(CultureInfo.InvariantCulture);
        }

        var sql = $"record_id IN ({string.Join(", ", names)})";
        void Binder(SqliteCommand cmd)
        {
            for (var i = 0; i < recordIds.Count; i++)
            {
                cmd.Parameters.AddWithValue(names[i], recordIds[i]);
            }
        }

        return (sql, Binder);
    }
}
