using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Evaluation;

namespace Harborline.Api.Foundation.RuleEngine.Context;

/// <summary>
/// Maps a flat dataset row into the rule engine's scope grammar for a view filter,
/// report computed column, or migration expression. These consumers share the existing
/// operator set and address row values through <c>field.</c> references or bare field names.
/// </summary>
public sealed class DatasetRowContextAdapter : IContextAdapter
{
    private readonly IReadOnlyDictionary<string, JsonNode?> _row;

    /// <summary>Constructs the adapter over a dataset row.</summary>
    public DatasetRowContextAdapter(IReadOnlyDictionary<string, JsonNode?> row)
        => _row = row ?? throw new ArgumentNullException(nameof(row));

    /// <inheritdoc />
    public IValueResolver CreateResolver(RuleEvalScope scope) => new ContextBagResolver(_row);
}
