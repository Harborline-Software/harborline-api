using Harborline.Api.Foundation.Documents.Merge;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Context;
using Harborline.Api.Foundation.RuleEngine.Evaluation;
using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.Documents.Rendering;

/// <summary>
/// Walks a <see cref="TemplateDefinition"/> block tree against a <see cref="DocumentMergeModel"/> and
/// produces a semantic <see cref="RenderedDocument"/> (#111 design §1.2/§3): for each merge field it asks
/// the <c>DocumentMergeContextAdapter</c>'s resolver for the <c>var</c> value at the block's scope and
/// formats it in the document locale; for each repeating region it <b>enumerates the rows from the record</b>
/// and asks for a row-scoped resolver per row (council F6); for each conditional block it evaluates the guard
/// through the same adapter. This is the forms reactive-graph loop, minus the reactivity — and there is
/// exactly one of it (§3.2, "no second renderer"): the issued document and the editor preview both walk here.
/// </summary>
public sealed class DocumentRenderWalker
{
    private readonly GuardEvaluator _guards;

    /// <summary>Constructs the walker over the Harborline rule-engine guard evaluator (the one interpreter).</summary>
    public DocumentRenderWalker(TimeProvider timeProvider, GuardEvaluator? guards = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _guards = guards ?? new GuardEvaluator(clock: timeProvider);
    }

    /// <summary>
    /// Renders <paramref name="template"/> against <paramref name="model"/> into a semantic document.
    /// </summary>
    /// <param name="template">The template block tree.</param>
    /// <param name="model">The record data (primary + related + repeating sections).</param>
    /// <param name="format">The document-locale format context (§1.4/§1.5).</param>
    /// <param name="namedRuleResolver">
    /// Optional resolver for a conditional block's named-Rule reference (the Rules pillar is the decision
    /// layer, §1.3). When a named-rule guard cannot be resolved (no resolver, or the rule is missing), the
    /// block is omitted (fail-closed — a "show when" guard defaults to not showing). Inline-expression
    /// guards need no resolver.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public RenderedDocument Render(
        TemplateDefinition template,
        DocumentMergeModel model,
        DocumentFormatContext format,
        Func<DocumentBlockGuard, RuleDefinition?>? namedRuleResolver = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(format);

        var adapter = new DocumentMergeContextAdapter(model);
        var rootResolver = adapter.CreateResolver(RuleEvalScope.Root);
        var blocks = new List<RenderedBlock>();

        foreach (var block in template.Structure)
        {
            // Conditional blocks (§1.3): a block guard evaluates at ROOT_SCOPE through the same adapter.
            if (!IsVisible(block.ShowWhen, adapter, namedRuleResolver, ct))
            {
                continue;
            }

            switch (block.Kind)
            {
                case DocumentBlockKind.RepeatingRegion:
                    blocks.Add(RenderTable(block, model, adapter, format));
                    break;

                default:
                    blocks.Add(RenderTextBlock(block, rootResolver, format));
                    break;
            }
        }

        var style = new RenderedStyle(template.Style?.BrandName);
        return new RenderedDocument(template.DocumentType, format.LocaleTag, format.CurrencyCode, style, blocks);
    }

    private RenderedTextBlock RenderTextBlock(DocumentBlock block, IValueResolver resolver, DocumentFormatContext format)
    {
        var lines = new List<RenderedLine>();
        foreach (var line in block.Lines ?? Array.Empty<DocumentLine>())
        {
            var runs = new List<RenderedRun>(line.Runs.Count);
            foreach (var run in line.Runs)
            {
                runs.Add(ResolveRun(run, resolver, format));
            }

            lines.Add(new RenderedLine(runs));
        }

        return new RenderedTextBlock(block.Kind, block.Label, lines);
    }

    private static RenderedTable RenderTable(
        DocumentBlock block,
        DocumentMergeModel model,
        IContextAdapter adapter,
        DocumentFormatContext format)
    {
        var columns = block.Columns ?? Array.Empty<DocumentColumn>();
        var section = block.RepeatSection ?? string.Empty;
        var headers = columns.Select(c => c.Header).ToList();
        var aligns = columns.Select(c => c.Align).ToList();

        var rows = new List<IReadOnlyList<RenderedCell>>();

        // Council F6: the WALKER enumerates the rows FROM THE RECORD; the resolver never enumerates. It
        // obtains a fresh row-scoped resolver per row (identical to a form child-collection).
        foreach (var row in model.Rows(section))
        {
            var rowResolver = adapter.CreateResolver(new RuleEvalScope(section, row.Id));
            var cells = new List<RenderedCell>(columns.Count);
            foreach (var col in columns)
            {
                var run = ResolveRun(DocumentInline.OfMerge(col.Binding, col.Format), rowResolver, format);
                cells.Add(new RenderedCell(run.Text, col.Align));
            }

            rows.Add(cells);
        }

        return new RenderedTable(block.Label, headers, aligns, rows);
    }

    private static RenderedRun ResolveRun(DocumentInline run, IValueResolver resolver, DocumentFormatContext format)
    {
        if (run.Kind == DocumentInlineKind.Literal)
        {
            return new RenderedRun(run.Literal ?? string.Empty);
        }

        var rv = resolver.ResolveVar(run.Binding ?? string.Empty);
        string text;
        if (rv.State == ValueState.Resolved)
        {
            text = rv.Value is null ? (run.FallbackLiteral ?? string.Empty) : format.Format(rv.Value, run.Format);
        }
        else
        {
            // A broken / errored binding degrades honestly to the fallback (never a silent wrong value);
            // the authoring-time needs-review flag (§5.4) is where a stale binding surfaces to the author.
            text = run.FallbackLiteral ?? string.Empty;
        }

        return new RenderedRun(text);
    }

    private bool IsVisible(
        DocumentBlockGuard? guard,
        IContextAdapter adapter,
        Func<DocumentBlockGuard, RuleDefinition?>? namedRuleResolver,
        CancellationToken ct)
    {
        if (guard is null)
        {
            return true;
        }

        RuleDefinition? rule = guard.Expression is { Length: > 0 } expr
            ? new RuleDefinition("template.block.guard", RuleTier.JsonLogic, RuleScope.Schema, string.Empty, expr, RuleActionKind.Validate)
            : namedRuleResolver?.Invoke(guard);

        // A named guard we cannot resolve fails closed: a "show when" condition defaults to NOT showing.
        if (rule is null)
        {
            return false;
        }

        return _guards.EvaluateGuard(rule, adapter, RuleEvalScope.Root, ct).Ok;
    }
}
