using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Schema;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// The record-type schemas the node's own write routes declare, registered in
/// <see cref="ISchemaRegistry"/> so the shipped validator can resolve them (ticket 151).
/// </summary>
/// <remarks>
/// A <see cref="SchemaId"/> is the content address of the schema text, so registering the same
/// text on every boot yields the same id; the registration is lazy and run once per process.
/// </remarks>
public sealed class NodeRecordSchemas(ISchemaRegistry registry)
{
    private static readonly string LegalEntitySchemaText =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "legal-entity",
          "type": "object",
          "required": ["legalName", "kind", "taxClassification"],
          "properties": {
            "legalName": { "type": "string", "minLength": 1, "maxLength": 512, "pattern": "\\S" },
            "kind": { "type": "string", "enum": [__KIND__] },
            "taxClassification": { "type": "string", "enum": [__TAX__] },
            "commonControlGroupId": { "type": ["string", "null"], "maxLength": 256 }
          }
        }
        """
            .Replace("__KIND__", Quoted(Enum.GetNames<EntityKind>()), StringComparison.Ordinal)
            .Replace("__TAX__", Quoted(Enum.GetNames<TaxClassification>()), StringComparison.Ordinal);

    private readonly Lazy<Task<SchemaId>> _legalEntity = new(
        async () => (await registry.RegisterAsync(
            LegalEntitySchemaText,
            tags: ["harborline.record-type"]).ConfigureAwait(false)).Id,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The registered schema id for a legal-entity record body.</summary>
    public Task<SchemaId> LegalEntityAsync() => _legalEntity.Value;

    private static string Quoted(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => $"\"{n}\""));
}
