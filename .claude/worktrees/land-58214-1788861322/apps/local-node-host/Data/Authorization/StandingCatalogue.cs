using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Schema;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>Surfaces the registered record schemas that carry a standing rule's input field.</summary>
public sealed class StandingCatalogue(ISchemaRegistry schemas)
{
    /// <summary>Lists every and only registered schema with the declared field in its root properties.</summary>
    public async Task<IReadOnlyList<SchemaId>> ListCarryingRecordTypesAsync(
        StandingRuleDefinition rule,
        string field,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (!rule.InputFields.Contains(field, StringComparer.Ordinal))
            throw new ArgumentException(
                $"Field '{field}' is not declared by standing rule '{rule.RuleId}'.",
                nameof(field));

        var result = new List<SchemaId>();
        await foreach (var schema in schemas.ListAsync(ct: cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(schema.JsonSchemaText);
            if (document.RootElement.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(field, out _))
                result.Add(schema.Id);
        }
        return result.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
    }
}
