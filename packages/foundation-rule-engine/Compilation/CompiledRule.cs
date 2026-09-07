using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Compilation;

/// <summary>A reference extracted from a lowered rule AST (SPINE-1 design §2.1).</summary>
internal abstract record RuleRef;

/// <summary>A top-level field reference (<c>field.x</c>; <c>parent.f</c> and <c>section.id.f</c> lower here).</summary>
internal sealed record FieldRef(string Name) : RuleRef;

/// <summary>A same-row sibling-field reference (<c>row.y</c>) — valid only in a <see cref="RuleScope.Row"/> rule.</summary>
internal sealed record RowFieldRef(string Field) : RuleRef;

/// <summary>A table-aggregate reference (<c>agg(fn, section, col)</c>).</summary>
internal sealed record AggRef(string Section, string Fn, string Col) : RuleRef;

/// <summary>
/// A single rule lowered + analysed at publish-time (SPINE-1 design §2.2 step 1).
/// Immutable; carried alongside the definition version.
/// </summary>
internal sealed record CompiledRule
{
    public required RuleDefinition Source { get; init; }

    /// <summary>The lowered, normalized JsonLogic AST (canonical <c>field.</c>/<c>row.</c> vars + <c>agg</c>).</summary>
    public required JsonNode? Ast { get; init; }

    /// <summary>The output type this rule produces.</summary>
    public required OutputType OutputType { get; init; }

    /// <summary>References extracted statically from <see cref="Ast"/>.</summary>
    public required IReadOnlyList<RuleRef> References { get; init; }

    /// <summary>The static target cell for Field/Section/Schema/Table rules; null for Row rules (resolved per-row).</summary>
    public CellAddress? StaticTarget { get; init; }

    /// <summary>For a Row rule: the owning child-table section id.</summary>
    public string? RowSection { get; init; }

    /// <summary>For a Row rule: the targeted row field.</summary>
    public string? RowField { get; init; }
}
