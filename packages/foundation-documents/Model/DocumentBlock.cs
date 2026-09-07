namespace Harborline.Api.Foundation.Documents.Model;

/// <summary>
/// One block in a <see cref="TemplateDefinition.Structure"/> tree (#111 design §1.1): a
/// <see cref="DocumentBlockKind.Header"/>, <see cref="DocumentBlockKind.Footer"/>,
/// <see cref="DocumentBlockKind.Section"/>, <see cref="DocumentBlockKind.FieldGrid"/>, or
/// <see cref="DocumentBlockKind.RepeatingRegion"/>. A block optionally carries a boolean
/// <see cref="ShowWhen"/> guard (§1.3) — a var-resolving rule evaluated through the same
/// <c>DocumentMergeContextAdapter</c>, never a per-pillar conditional operator.
/// </summary>
/// <remarks>
/// A single <c>Kind</c>-tagged record (rather than a polymorphic hierarchy) keeps the block-tree a flat,
/// canonical-JSON-round-trippable shape — the pinned pack-content contract the projector parses. The
/// walker (<c>DocumentRenderWalker</c>) reads the arm matching <c>Kind</c>; the other arms are null.
/// </remarks>
public sealed record DocumentBlock
{
    /// <summary>Which block shape this is.</summary>
    public required DocumentBlockKind Kind { get; init; }

    /// <summary>Optional block heading (e.g. <c>BILL TO</c>, <c>LINE ITEMS</c>) — a literal or localizable code.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The content lines for a <see cref="DocumentBlockKind.Header"/> / <see cref="DocumentBlockKind.Footer"/> /
    /// <see cref="DocumentBlockKind.Section"/> / <see cref="DocumentBlockKind.FieldGrid"/> block: an ordered
    /// list of lines, each a sequence of literal + merge runs resolved at <c>ROOT_SCOPE</c>.
    /// </summary>
    public IReadOnlyList<DocumentLine>? Lines { get; init; }

    /// <summary>
    /// The model section id a <see cref="DocumentBlockKind.RepeatingRegion"/> iterates (e.g. <c>lineItems</c>).
    /// The walker enumerates this section's rows <b>from the record</b> and produces a row-scoped resolver
    /// per row (design §1.2 / council F6). Null on non-repeating blocks.
    /// </summary>
    public string? RepeatSection { get; init; }

    /// <summary>The per-row columns of a <see cref="DocumentBlockKind.RepeatingRegion"/>; each binds a <c>row.</c> path.</summary>
    public IReadOnlyList<DocumentColumn>? Columns { get; init; }

    /// <summary>Optional boolean guard (§1.3): when present and false, the block is omitted.</summary>
    public DocumentBlockGuard? ShowWhen { get; init; }
}

/// <summary>The shape of a <see cref="DocumentBlock"/> (§1.1).</summary>
public enum DocumentBlockKind
{
    /// <summary>The document masthead — brand + primary header merge fields.</summary>
    Header = 0,

    /// <summary>The document footer — terms / boilerplate / notes.</summary>
    Footer = 1,

    /// <summary>Static or merge-bearing prose.</summary>
    Section = 2,

    /// <summary>Label/value pairs (a "Bill to" address block, a totals block).</summary>
    FieldGrid = 3,

    /// <summary>The line-item table — repeats once per row of <see cref="DocumentBlock.RepeatSection"/>.</summary>
    RepeatingRegion = 4,
}

/// <summary>One line of a text block: an ordered sequence of literal + merge runs.</summary>
/// <param name="Runs">The inline runs, concatenated left-to-right.</param>
public sealed record DocumentLine(IReadOnlyList<DocumentInline> Runs)
{
    /// <summary>A convenience line of a single literal run (a boilerplate line).</summary>
    public static DocumentLine Literal(string text) => new(new[] { DocumentInline.OfLiteral(text) });

    /// <summary>A convenience "label: {merge}" line (a field-grid row).</summary>
    public static DocumentLine LabelValue(string label, string binding, MergeFormat format = MergeFormat.Text)
        => new(new[] { DocumentInline.OfLiteral(label), DocumentInline.OfMerge(binding, format) });
}

/// <summary>An inline run within a <see cref="DocumentLine"/>: either literal text or a merge field.</summary>
/// <remarks>
/// A merge run stores a scope-grammar <c>var</c> path (§1.2) — never a <c>{{mustache}}</c> free-text
/// fragment. It is resolved by asking the pillar's <c>IValueResolver</c> for the path and formatting the
/// canonical value via the document-locale format cascade (§1.4). There is no second templating DSL.
/// </remarks>
public sealed record DocumentInline
{
    /// <summary>Whether this run is a literal or a resolved merge field.</summary>
    public required DocumentInlineKind Kind { get; init; }

    /// <summary>The literal text (when <see cref="DocumentInlineKind.Literal"/>).</summary>
    public string? Literal { get; init; }

    /// <summary>The scope-grammar <c>var</c> path (when <see cref="DocumentInlineKind.Merge"/>) — e.g. <c>field.customerName</c>, <c>row.description</c>.</summary>
    public string? Binding { get; init; }

    /// <summary>How the resolved canonical value is formatted in the document locale (§1.4).</summary>
    public MergeFormat Format { get; init; } = MergeFormat.Text;

    /// <summary>Optional literal shown when the merge resolves to null — an honest fallback, never a silent blank (§5.7).</summary>
    public string? FallbackLiteral { get; init; }

    /// <summary>A literal run.</summary>
    public static DocumentInline OfLiteral(string text) => new() { Kind = DocumentInlineKind.Literal, Literal = text };

    /// <summary>A merge run over a var path.</summary>
    public static DocumentInline OfMerge(string binding, MergeFormat format = MergeFormat.Text, string? fallback = null)
        => new() { Kind = DocumentInlineKind.Merge, Binding = binding, Format = format, FallbackLiteral = fallback };
}

/// <summary>Whether a <see cref="DocumentInline"/> run is literal or a merge field.</summary>
public enum DocumentInlineKind
{
    /// <summary>Verbatim text.</summary>
    Literal = 0,

    /// <summary>A merge field resolved from the record via a <c>var</c> path.</summary>
    Merge = 1,
}

/// <summary>One column of a <see cref="DocumentBlockKind.RepeatingRegion"/> (a line-item table column).</summary>
/// <param name="Header">The column header (literal or localizable code).</param>
/// <param name="Binding">The <c>row.</c> path bound to this column (e.g. <c>row.description</c>, <c>row.amount</c>).</param>
/// <param name="Format">How the resolved cell value is formatted in the document locale.</param>
/// <param name="Align">Text alignment (numbers right-aligned by convention).</param>
public sealed record DocumentColumn(
    string Header,
    string Binding,
    MergeFormat Format = MergeFormat.Text,
    ColumnAlign Align = ColumnAlign.Start);

/// <summary>Cell alignment for a table column.</summary>
public enum ColumnAlign
{
    /// <summary>Leading edge (left in LTR).</summary>
    Start = 0,

    /// <summary>Centered.</summary>
    Center = 1,

    /// <summary>Trailing edge (right in LTR) — the convention for money/number columns.</summary>
    End = 2,
}

/// <summary>
/// How a resolved canonical merge value is formatted in the document locale (§1.4). The value is always
/// stored canonically (ISO-8601 date, decimal money, raw number, string); format is a property of the
/// rendering context (the cascade), never the template.
/// </summary>
public enum MergeFormat
{
    /// <summary>Verbatim string.</summary>
    Text = 0,

    /// <summary>Currency — the document-locale currency format over the decimal value + currency code.</summary>
    Currency = 1,

    /// <summary>Date — the document-locale short-date format over an ISO-8601 value.</summary>
    Date = 2,

    /// <summary>Integer — grouped, no fraction.</summary>
    Integer = 3,

    /// <summary>Decimal — the document-locale number format.</summary>
    Decimal = 4,
}

/// <summary>
/// A conditional-block guard (§1.3): a boolean rule evaluated through the <c>DocumentMergeContextAdapter</c>.
/// Either an <see cref="Expression"/> (an inline shipyard-jsonlogic/v1 boolean) OR a reference to a shared
/// named Rule (<see cref="NamedRuleKey"/> + <see cref="NamedRuleVersion"/>) from the Rules pillar. The guard
/// resolves the same <c>var</c> references the merge fields do — no per-pillar conditional operators.
/// </summary>
/// <param name="Expression">Inline shipyard-jsonlogic/v1 boolean expression (canonical JSON text). Mutually exclusive with the named ref.</param>
/// <param name="NamedRuleKey">A shared named-Rule key resolved via <c>IRuleRegistry</c> (the Rules pillar is the decision layer).</param>
/// <param name="NamedRuleVersion">The named-Rule version to pin (null = latest published).</param>
public sealed record DocumentBlockGuard(
    string? Expression = null,
    string? NamedRuleKey = null,
    string? NamedRuleVersion = null)
{
    /// <summary>A guard from an inline boolean expression.</summary>
    public static DocumentBlockGuard Inline(string expression) => new(Expression: expression);

    /// <summary>A guard that references a shared named Rule.</summary>
    public static DocumentBlockGuard Named(string key, string? version = null) => new(NamedRuleKey: key, NamedRuleVersion: version);
}
