using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.RuleEngine.Graph;

/// <summary>One child-table row in a <see cref="RuleInstance"/>.</summary>
public sealed class RuleRow
{
    public RuleRow(string id, IReadOnlyDictionary<string, JsonNode?> fields)
    {
        Id = id;
        Fields = new Dictionary<string, JsonNode?>(fields);
    }

    /// <summary>Stable row id (the row's <c>_id</c> property, else its ordinal at load).</summary>
    public string Id { get; }

    /// <summary>The row's cell values, keyed by field name.</summary>
    public Dictionary<string, JsonNode?> Fields { get; }
}

/// <summary>
/// A form instance the rule graph evaluates over (SPINE-1 design §2.1): top-level
/// fields plus child tables (ordered rows). Decoupled from the persistence /
/// FormDefinition shape — the engine operates on this abstract value tree, so it
/// does not depend on the FORM-2 child-table build state.
/// </summary>
public sealed class RuleInstance
{
    /// <summary>Top-level field values, keyed by field name.</summary>
    public Dictionary<string, JsonNode?> Fields { get; } = new();

    /// <summary>Child tables, keyed by section id; each is an ordered list of rows.</summary>
    public Dictionary<string, List<RuleRow>> Tables { get; } = new();

    /// <summary>
    /// Builds an instance from a JSON object: a property whose value is an array of
    /// objects is a child table (section id = property name; row id = each element's
    /// <c>_id</c> string, else its index); every other property is a top-level field.
    /// </summary>
    public static RuleInstance FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var instance = new RuleInstance();
        foreach (var (key, value) in json)
        {
            if (value is JsonArray arr && arr.Count > 0 && arr.All(e => e is JsonObject))
            {
                var rows = new List<RuleRow>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var rowObj = (JsonObject)arr[i]!;
                    string id = rowObj.TryGetPropertyValue("_id", out var idNode) && idNode is not null
                        ? idNode.GetValue<string>()
                        : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var fields = new Dictionary<string, JsonNode?>();
                    foreach (var (fk, fv) in rowObj)
                    {
                        if (fk == "_id") continue;
                        fields[fk] = fv?.DeepClone();
                    }
                    rows.Add(new RuleRow(id, fields));
                }
                instance.Tables[key] = rows;
            }
            else
            {
                instance.Fields[key] = value?.DeepClone();
            }
        }
        return instance;
    }
}
