using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Documents.Rendering;

namespace Harborline.Api.Documents.PdfSharp;

/// <summary>
/// The first <see cref="IPdfExportWriter"/> implementation (#111 design §3.4): renders a semantic
/// <see cref="RenderedDocument"/> to PDF bytes via PDFsharp + MigraDoc (MIT, pure-managed). Registered on
/// the desktop / dogfood JIT node; a mobile carve would register the AOT-clean minimal emitter behind the
/// same contract (§3.4). Deterministic in <i>content</i> (the same semantic document renders the same text
/// + structure); PDF byte-level fields (timestamps, subset ordering) are non-deterministic by design — the
/// stored bytes are the authoritative artifact and a re-render is content-equivalent, not byte-equivalent
/// (council F3).
/// </summary>
public sealed class PdfSharpDocumentWriter : IPdfExportWriter
{
    /// <inheritdoc />
    public ValueTask<byte[]> WriteAsync(RenderedDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ct.ThrowIfCancellationRequested();

        if (!DocumentFontResolver.TryInstall())
        {
            throw new InvalidOperationException(
                "No usable system font was found for PDF rendering. The desktop/dogfood node ships with "
                + "fonts; install a TrueType sans font (or register a font resolver) to render documents on "
                + "this host.");
        }

        var doc = BuildDocument(document);
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return ValueTask.FromResult(stream.ToArray());
    }

    private static Document BuildDocument(RenderedDocument rendered)
    {
        var doc = new Document();
        var normal = doc.Styles["Normal"]!; // the Normal style is always created with the document.
        normal.Font.Name = DocumentFontResolver.FamilyName;
        normal.Font.Size = 10;

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.Letter;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.2);

        if (rendered.Style.BrandName is { Length: > 0 } brand)
        {
            var masthead = section.AddParagraph(brand);
            masthead.Format.Font.Size = 18;
            masthead.Format.Font.Bold = true;
            masthead.Format.SpaceAfter = Unit.FromPoint(8);
        }

        foreach (var block in rendered.Blocks)
        {
            switch (block)
            {
                case RenderedTextBlock text:
                    AddTextBlock(section, text);
                    break;

                case RenderedTable table:
                    AddTable(section, table);
                    break;
            }
        }

        return doc;
    }

    private static void AddTextBlock(Section section, RenderedTextBlock block)
    {
        if (block.Label is { Length: > 0 } label)
        {
            var heading = section.AddParagraph(label);
            heading.Format.Font.Bold = true;
            heading.Format.SpaceBefore = Unit.FromPoint(8);
            heading.Format.SpaceAfter = Unit.FromPoint(2);
        }

        foreach (var line in block.Lines)
        {
            var paragraph = section.AddParagraph();
            if (block.Kind == DocumentBlockKind.Header)
            {
                paragraph.Format.Font.Size = 11;
            }

            foreach (var run in line.Runs)
            {
                if (run.Emphasis)
                {
                    paragraph.AddFormattedText(run.Text, TextFormat.Bold);
                }
                else
                {
                    paragraph.AddText(run.Text);
                }
            }
        }
    }

    private static void AddTable(Section section, RenderedTable rendered)
    {
        if (rendered.Label is { Length: > 0 } label)
        {
            var heading = section.AddParagraph(label);
            heading.Format.Font.Bold = true;
            heading.Format.SpaceBefore = Unit.FromPoint(10);
            heading.Format.SpaceAfter = Unit.FromPoint(2);
        }

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.LightGray;

        var columnCount = Math.Max(rendered.Headers.Count, 1);

        // Distribute the printable width: the leading (description) column takes ~44%, the rest split the
        // remainder. Deterministic → content-equivalent layout across renders.
        const double printableCm = 16.6; // Letter minus 2.2cm margins each side.
        var firstWidth = columnCount == 1 ? printableCm : printableCm * 0.44;
        var restWidth = columnCount == 1 ? printableCm : (printableCm - firstWidth) / (columnCount - 1);

        for (var i = 0; i < columnCount; i++)
        {
            var column = table.AddColumn(Unit.FromCentimeter(i == 0 ? firstWidth : restWidth));
            column.Format.Alignment = MapAlign(i < rendered.Aligns.Count ? rendered.Aligns[i] : ColumnAlign.Start);
        }

        var header = table.AddRow();
        header.Format.Font.Bold = true;
        for (var i = 0; i < rendered.Headers.Count; i++)
        {
            header.Cells[i].AddParagraph(rendered.Headers[i]);
        }

        foreach (var row in rendered.Rows)
        {
            var dataRow = table.AddRow();
            for (var i = 0; i < row.Count && i < columnCount; i++)
            {
                dataRow.Cells[i].AddParagraph(row[i].Text);
            }
        }
    }

    private static ParagraphAlignment MapAlign(ColumnAlign align) => align switch
    {
        ColumnAlign.Center => ParagraphAlignment.Center,
        ColumnAlign.End => ParagraphAlignment.Right,
        _ => ParagraphAlignment.Left,
    };
}
