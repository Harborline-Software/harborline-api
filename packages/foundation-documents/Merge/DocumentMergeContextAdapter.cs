using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Context;
using Harborline.Api.Foundation.RuleEngine.Evaluation;
using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.Documents.Merge;

/// <summary>
/// The <b>third</b> <see cref="IContextAdapter"/> implementation (#111 design §1.2; the seam is ADR 0146 D3,
/// which names <c>document-merge-context</c> as this pillar's #111 deliverable). Maps a
/// <see cref="DocumentMergeModel"/> — primary record + flattened related records + repeating line-item
/// sections — into the rule engine's scope grammar, so a template's merge fields and block guards resolve
/// through the <i>one</i> engine (own nothing about the operator set; adapt the record shape). It is a
/// faithful sibling of the forms <c>FormContextAdapter</c>/<c>CellResolver</c> and the workflow-guard
/// <c>ContextBagAdapter</c>, written against ONLY the public seam (they are internal to the engine assembly).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scopes.</b> <c>ROOT_SCOPE</c> (<see cref="RuleEvalScope.Root"/>) resolves header/footer/section
/// <c>field.</c> references; a row scope (<c>{RowSection, RowId}</c>) resolves a line item's <c>row.</c>
/// references — identical to how a form resolves a child-collection row. The block-tree walker (never the
/// resolver) enumerates the rows from the record and asks for a resolver per row (council F6).
/// </para>
/// <para>
/// <b>Aggregation is fail-closed (council F5 made structural).</b> <see cref="Resolver.ResolveAgg"/>
/// returns a bad-reference: a money document merges the record's <i>authoritative stored</i>
/// subtotal/tax/total (a plain <c>var</c>), and a render-time <c>agg</c> re-sum of a posted total is
/// disallowed — it could diverge (rounding, tax treatment) from the ledger-authoritative figure, printing
/// a total that disagrees with the posted invoice. Within-instance aggregation, when legitimately needed
/// on a non-money surface, is computed upstream in the rule/record layer and merged as a stored field
/// (ADR 0146 D4), not folded at render time. Matches the workflow-guard <c>ContextBagResolver</c> posture.
/// </para>
/// </remarks>
public sealed class DocumentMergeContextAdapter : IContextAdapter
{
    private readonly DocumentMergeModel _model;

    /// <summary>Constructs the adapter over a merge model.</summary>
    public DocumentMergeContextAdapter(DocumentMergeModel model)
        => _model = model ?? throw new ArgumentNullException(nameof(model));

    /// <inheritdoc />
    public IValueResolver CreateResolver(RuleEvalScope scope) => new Resolver(_model, scope.RowSection, scope.RowId);

    private sealed class Resolver : IValueResolver
    {
        private readonly DocumentMergeModel _model;
        private readonly string? _rowSection;
        private readonly string? _rowId;

        public Resolver(DocumentMergeModel model, string? rowSection, string? rowId)
        {
            _model = model;
            _rowSection = rowSection;
            _rowId = rowId;
        }

        public RefValue ResolveVar(string path)
        {
            if (path.StartsWith("row.", StringComparison.Ordinal))
            {
                // A row reference requires a row scope; at ROOT it is a bad reference (as the forms /
                // workflow resolvers behave) — the walker only asks for row.* at a row scope.
                if (_rowSection is null || _rowId is null)
                {
                    return RefValue.OfError(RuleError.Of(RuleEngineCodes.BadReference, "path", path));
                }

                var field = path["row.".Length..];
                var row = FindRow(_rowSection, _rowId);
                if (row is not null && row.Fields.TryGetValue(field, out var rv))
                {
                    return RefValue.Resolved(rv);
                }

                // Absent row field = resolved-null (open-world), same as a missing top-level field.
                return RefValue.Resolved(null);
            }

            // "field.x" and a bare "x" both address the root record (dotted keys for related records).
            var name = path.StartsWith("field.", StringComparison.Ordinal) ? path["field.".Length..] : path;
            return _model.RootFields.TryGetValue(name, out var v)
                ? RefValue.Resolved(v)
                : RefValue.Resolved(null);
        }

        public RefValue ResolveAgg(string fn, string section, string col)
            // Fail-closed (council F5): no render-time aggregation on a money document — merge the
            // record's authoritative stored total, never a re-summed figure that could diverge.
            // Built through the tier's one refusal constructor (ticket 162) so the params cannot drift.
            => RefValue.UnavailableAggregate(fn, section, col);

        private DocumentMergeRow? FindRow(string section, string rowId)
        {
            foreach (var row in _model.Rows(section))
            {
                if (string.Equals(row.Id, rowId, StringComparison.Ordinal))
                {
                    return row;
                }
            }

            return null;
        }
    }
}
