using System;
using System.Collections.Generic;

using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Data.Search.Generation;

/// <summary>
/// The clipped grounding assembler (ADR 0135 KG-search Slice 2-foundation, G-G2) — assembles the grounding fed
/// to the generation provider from a permission-clipped set, so the model ONLY EVER sees authorized content. A
/// forbidden record can NEVER reach the grounding.
/// </summary>
/// <remarks>
/// <para>
/// <b>The load-bearing G-G2 property: the assembler accepts an <see cref="AuthorizedRecordScope"/>, NOT a bare
/// id-list.</b> The clipped grounding-text retrieval happens upstream in
/// <see cref="NodeVecSearchReadService.RetrieveClippedGroundingAsync"/> (the SOLE clipped reader of the
/// <c>search_nodes</c> content table — the assembler does NOT issue a raw content read, which the
/// <c>SearchClipArchFence</c> forbids). This assembler's job is to carry the scope STRUCTURALLY: its
/// <see cref="Assemble"/> takes the resolved scope (inside a <see cref="ClippedGrounding"/>) and emits grounding
/// ONLY for rows the scope <see cref="AuthorizedRecordScope.Authorizes"/> — a defence-in-depth re-clip mirroring
/// the read service's discipline. An <c>Assemble</c> overload that ran against a bare id-list with no scope would
/// re-open the graph-tunnel channel the clip already closed (bug-1354/1355); there is no such overload.
/// </para>
/// <para>
/// <b>Asserted vs inferred provenance (§2.9 det/ai split).</b> Slice-2-foundation grounding comes from real
/// indexed records (<c>search_nodes</c> rows), so it is ASSERTED (facts, from the records). Inferred-edge
/// grounding (AI-suggested similarity) is a later sub-slice; when it arrives it rides
/// <see cref="KgGroundingSource.Asserted"/> = false and is never authoritative for a guarded action. The
/// provenance is carried onto each grounding source so the downstream proposal basis can surface it.
/// </para>
/// </remarks>
public sealed class GroundingAssembler
{
    /// <summary>
    /// Assemble the grounding sources from a permission-clipped retrieval (G-G2). Emits a
    /// <see cref="KgGroundingSource"/> ONLY for each row whose record id the clip
    /// <paramref name="clipped"/>.<see cref="ClippedGrounding.Scope"/> authorizes — a forbidden record never
    /// reaches the grounding.
    /// </summary>
    /// <param name="clipped">
    /// The clipped grounding (carries the fail-closed <see cref="AuthorizedRecordScope"/> + the authorized rows).
    /// This is the structural G-G2 input — the assembler takes the SCOPE, never a bare id-list.
    /// </param>
    /// <returns>The grounding sources the model may see — all authorized, taint-bearing (UNTRUSTED text).</returns>
    public IReadOnlyList<KgGroundingSource> Assemble(ClippedGrounding clipped)
    {
        ArgumentNullException.ThrowIfNull(clipped);

        var scope = clipped.Scope;
        var sources = new List<KgGroundingSource>(clipped.Rows.Count);
        foreach (var row in clipped.Rows)
        {
            // DEFENCE IN DEPTH (G-G2): re-assert the clip. Even though RetrieveClippedGroundingAsync already
            // filtered to authorized rows, the assembler will NOT emit a source the scope does not authorize —
            // so an un-clipped row (a future bug, or a hand-built ClippedGrounding) is structurally dropped here,
            // never handed to the model. This is the mirror of the read service's belt-and-braces re-clip.
            if (!scope.Authorizes(row.RecordId))
            {
                continue;
            }

            // Slice-2-foundation grounding is from real indexed records ⇒ asserted (a fact). Inferred-edge
            // grounding is a later sub-slice (asserted: false).
            sources.Add(new KgGroundingSource(row.RecordId, row.Text, Asserted: true));
        }

        return sources;
    }
}
