using System.Text;

namespace Harborline.Api.Foundation.Documents.Rendering;

/// <summary>
/// The semantic result of walking a template against a record (#111 design §3): an ordered tree of
/// laid-out blocks whose merge fields are resolved and formatted, but which is <b>library-neutral</b> —
/// it carries no PDF concept. An <c>IPdfExportWriter</c> adapter emits bytes from this; the same semantic
/// model is what a print or email-body channel consumes (§3.5), and what the editor preview renders back.
/// </summary>
/// <remarks>
/// This is the "semantic content objects" ADR 0021 §1 calls for, and the anchor for the content-equivalence
/// contract (council F3): two renders of the same pinned template version against the same pinned data
/// produce an <b>equal</b> <see cref="RenderedDocument"/> (see <see cref="ExtractText"/>) — the stored PDF
/// bytes are the authoritative artifact, but the observable <i>content</i> is this model, which is
/// deterministic where PDF bytes (timestamps, font-subset ordering, library version) are not.
/// </remarks>
/// <param name="DocumentType">The document type rendered (invoice/statement/…).</param>
/// <param name="LocaleTag">The document locale the values were formatted in (§1.5).</param>
/// <param name="CurrencyCode">The currency code used for money formatting (null if none).</param>
/// <param name="Style">The resolved visual identity (brand masthead) — template data, a separate register (§1.6).</param>
/// <param name="Blocks">The laid-out blocks in document order.</param>
public sealed record RenderedDocument(
    string DocumentType,
    string LocaleTag,
    string? CurrencyCode,
    RenderedStyle Style,
    IReadOnlyList<RenderedBlock> Blocks)
{
    /// <summary>
    /// A canonical text projection of every run + cell, in document order — the observable content used to
    /// assert "the PDF contains the line items + totals" and to compare two renders for content-equivalence
    /// (F3). Deterministic: independent of PDF byte-level non-determinism.
    /// </summary>
    public string ExtractText()
    {
        var sb = new StringBuilder();
        if (Style.BrandName is { Length: > 0 } brand)
        {
            sb.Append(brand).Append('\n');
        }

        foreach (var block in Blocks)
        {
            switch (block)
            {
                case RenderedTextBlock text:
                    if (text.Label is { Length: > 0 } label)
                    {
                        sb.Append(label).Append('\n');
                    }

                    foreach (var line in text.Lines)
                    {
                        foreach (var run in line.Runs)
                        {
                            sb.Append(run.Text);
                        }

                        sb.Append('\n');
                    }

                    break;

                case RenderedTable table:
                    if (table.Label is { Length: > 0 } tlabel)
                    {
                        sb.Append(tlabel).Append('\n');
                    }

                    sb.Append(string.Join('\t', table.Headers)).Append('\n');
                    foreach (var row in table.Rows)
                    {
                        sb.Append(string.Join('\t', row.Select(c => c.Text))).Append('\n');
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>The resolved visual identity carried into the render (masthead brand; logo arrives with D3).</summary>
/// <param name="BrandName">The brand printed in the document masthead, or null.</param>
public sealed record RenderedStyle(string? BrandName);

/// <summary>Base type for a laid-out block in a <see cref="RenderedDocument"/>.</summary>
public abstract record RenderedBlock;

/// <summary>A laid-out text block (header/footer/section/field-grid) — a heading plus resolved lines.</summary>
/// <param name="Kind">The originating block kind.</param>
/// <param name="Label">The block heading (already localized/literal), or null.</param>
/// <param name="Lines">The resolved lines.</param>
public sealed record RenderedTextBlock(
    Model.DocumentBlockKind Kind,
    string? Label,
    IReadOnlyList<RenderedLine> Lines) : RenderedBlock;

/// <summary>A laid-out line-item table (a repeating region) — headers + resolved rows.</summary>
/// <param name="Label">The table heading, or null.</param>
/// <param name="Headers">The column headers in order.</param>
/// <param name="Aligns">Per-column alignment, parallel to <paramref name="Headers"/>.</param>
/// <param name="Rows">The resolved rows; each is a list of cells parallel to <paramref name="Headers"/>.</param>
public sealed record RenderedTable(
    string? Label,
    IReadOnlyList<string> Headers,
    IReadOnlyList<Model.ColumnAlign> Aligns,
    IReadOnlyList<IReadOnlyList<RenderedCell>> Rows) : RenderedBlock;

/// <summary>One resolved line: an ordered sequence of text runs.</summary>
/// <param name="Runs">The runs, concatenated left-to-right.</param>
public sealed record RenderedLine(IReadOnlyList<RenderedRun> Runs);

/// <summary>A resolved run of text (a literal or a formatted merge value).</summary>
/// <param name="Text">The final text.</param>
/// <param name="Emphasis">Whether the run is emphasized (a label vs a value) — a hint for the writer.</param>
public sealed record RenderedRun(string Text, bool Emphasis = false);

/// <summary>A resolved table cell.</summary>
/// <param name="Text">The final formatted cell text.</param>
/// <param name="Align">The cell alignment.</param>
public sealed record RenderedCell(string Text, Model.ColumnAlign Align);
