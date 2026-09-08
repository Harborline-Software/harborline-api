namespace Harborline.Api.Foundation.RuleEngine.Model;

/// <summary>
/// The grain of an <see cref="CellAddress"/> — which kind of addressable cell
/// a rule targets or references (SPINE-1 design §2.1).
/// </summary>
public enum CellKind
{
    /// <summary>A top-level form field — <c>field:&lt;name&gt;</c>.</summary>
    Field = 0,

    /// <summary>A child-table row's cell — <c>row:&lt;sectionId&gt;/&lt;rowId&gt;/&lt;field&gt;</c>.</summary>
    Row = 1,

    /// <summary>A table aggregate pseudo-cell — <c>agg:&lt;sectionId&gt;/&lt;fn&gt;/&lt;col&gt;</c>.</summary>
    TableAggregate = 2,

    /// <summary>A section-scoped pseudo-cell (e.g. whole-section visibility) — <c>section:&lt;id&gt;</c>.</summary>
    Section = 3,

    /// <summary>The whole-schema pseudo-cell — <c>schema:</c>.</summary>
    Schema = 4,
}

/// <summary>
/// An addressable cell in a form instance's dependency graph (SPINE-1 design
/// §2.1). The node identity of the graph evaluator. Value-equal + ordered by its
/// canonical <see cref="Key"/> so it can key a dictionary and serialize stably.
/// </summary>
/// <remarks>
/// The canonical string form (<see cref="Key"/>) is the single cross-tier key used
/// in the conformance corpus' <c>expectedOutcomes</c> map; the TS engine produces
/// byte-identical keys.
/// </remarks>
public readonly record struct CellAddress
{
    private CellAddress(CellKind kind, string sectionId, string rowId, string name)
    {
        Kind = kind;
        SectionId = sectionId;
        RowId = rowId;
        Name = name;
    }

    /// <summary>The cell grain.</summary>
    public CellKind Kind { get; }

    /// <summary>Owning child-table section id (Row / TableAggregate / Section); else empty.</summary>
    public string SectionId { get; }

    /// <summary>Stable row id within the child table (Row only); else empty.</summary>
    public string RowId { get; }

    /// <summary>
    /// Field name (Field / Row), aggregate <c>"&lt;fn&gt;/&lt;col&gt;"</c> tail (TableAggregate);
    /// else empty.
    /// </summary>
    public string Name { get; }

    /// <summary>A top-level field cell.</summary>
    public static CellAddress Field(string name) => new(CellKind.Field, "", "", name);

    /// <summary>A child-table row cell.</summary>
    public static CellAddress Row(string sectionId, string rowId, string field)
        => new(CellKind.Row, sectionId, rowId, field);

    /// <summary>A table-aggregate pseudo-cell (<paramref name="fn"/> over <paramref name="col"/>).</summary>
    public static CellAddress TableAggregate(string sectionId, string fn, string col)
        => new(CellKind.TableAggregate, sectionId, "", fn + "/" + col);

    /// <summary>A section-scoped pseudo-cell.</summary>
    public static CellAddress Section(string sectionId) => new(CellKind.Section, sectionId, "", "");

    /// <summary>The whole-schema pseudo-cell.</summary>
    public static CellAddress Schema() => new(CellKind.Schema, "", "", "");

    /// <summary>
    /// The canonical, stable string key (cross-tier identical). Forms:
    /// <c>field:name</c>, <c>row:section/rowId/field</c>, <c>agg:section/fn/col</c>,
    /// <c>section:id</c>, <c>schema:</c>.
    /// </summary>
    public string Key => Kind switch
    {
        CellKind.Field => "field:" + Name,
        CellKind.Row => "row:" + SectionId + "/" + RowId + "/" + Name,
        CellKind.TableAggregate => "agg:" + SectionId + "/" + Name,
        CellKind.Section => "section:" + SectionId,
        CellKind.Schema => "schema:",
        _ => "?:",
    };

    /// <inheritdoc />
    public override string ToString() => Key;
}
