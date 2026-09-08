using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Documents.Merge;

/// <summary>
/// The record data a template merges from, mapped into the rule engine's scope grammar (#111 design §1.2).
/// The primary record (plus flattened related records) are the <see cref="RootFields"/>; each repeating
/// sub-collection (the line items) is a named section of <see cref="DocumentMergeRow"/>s. This is the
/// documents pillar's own decoupled shape — it deliberately mirrors the forms <c>RuleInstance</c>
/// (Fields + Tables) so the <c>DocumentMergeContextAdapter</c> resolves <c>field.</c>/<c>row.</c>
/// references identically, but it carries no forms/invoice dependency (own nothing, adapt the record shape).
/// </summary>
/// <remarks>
/// Related-record fields are pre-flattened into <see cref="RootFields"/> with dotted keys
/// (<c>customer.name</c>, <c>customer.email</c>), addressed as <c>field.customer.name</c>. Row fields are
/// addressed as <c>row.description</c> at the row's scope. All values are canonical JSON (ISO-8601 date,
/// decimal-string money, raw number, string) — formatting is the renderer's job, not the model's.
/// </remarks>
public sealed class DocumentMergeModel
{
    /// <summary>The primary record + flattened related-record fields (dotted keys), addressed at ROOT_SCOPE.</summary>
    public IReadOnlyDictionary<string, JsonNode?> RootFields { get; }

    /// <summary>The repeating sub-collections keyed by section id (e.g. <c>lineItems</c>); rows in display order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DocumentMergeRow>> Sections { get; }

    /// <summary>Constructs a merge model over its root fields and repeating sections.</summary>
    public DocumentMergeModel(
        IReadOnlyDictionary<string, JsonNode?> rootFields,
        IReadOnlyDictionary<string, IReadOnlyList<DocumentMergeRow>> sections)
    {
        RootFields = rootFields ?? throw new ArgumentNullException(nameof(rootFields));
        Sections = sections ?? throw new ArgumentNullException(nameof(sections));
    }

    /// <summary>The rows of <paramref name="section"/>, or an empty list if the section is absent (open-world).</summary>
    public IReadOnlyList<DocumentMergeRow> Rows(string section)
        => Sections.TryGetValue(section, out var rows) ? rows : Array.Empty<DocumentMergeRow>();

    /// <summary>A fluent builder — the record→model mapping step a pillar consumer (the invoice endpoint) runs.</summary>
    public static Builder Build() => new();

    /// <summary>Fluent builder for a <see cref="DocumentMergeModel"/>.</summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, JsonNode?> _root = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<DocumentMergeRow>> _sections = new(StringComparer.Ordinal);

        /// <summary>Sets a root field (dotted keys allowed for related records).</summary>
        public Builder Field(string key, JsonNode? value)
        {
            _root[key] = value;
            return this;
        }

        /// <summary>Sets a root field from a string (null value = absent → resolves open-world null).</summary>
        public Builder Field(string key, string? value)
            => Field(key, value is null ? null : JsonValue.Create(value));

        /// <summary>Sets a repeating section's rows.</summary>
        public Builder Section(string section, IReadOnlyList<DocumentMergeRow> rows)
        {
            _sections[section] = rows;
            return this;
        }

        /// <summary>Materializes the model.</summary>
        public DocumentMergeModel ToModel() => new(_root, _sections);
    }
}

/// <summary>
/// One row of a repeating section (a line item). Its <see cref="Fields"/> are addressed as <c>row.&lt;field&gt;</c>
/// at the row's scope; its <see cref="Id"/> is the stable row identity the walker passes as
/// <c>RuleEvalScope.RowId</c> (design §1.2, mirroring a form child-collection row).
/// </summary>
/// <param name="Id">Stable row identity (the walker enumerates rows and scopes a resolver per row).</param>
/// <param name="Fields">The row's canonical-JSON field values.</param>
public sealed record DocumentMergeRow(string Id, IReadOnlyDictionary<string, JsonNode?> Fields);
