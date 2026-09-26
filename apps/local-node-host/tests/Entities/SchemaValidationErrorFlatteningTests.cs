using System.Text;
using System.Text.Json;

using Harborline.Api.Kernel.Schema;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// CodeRabbit 4112934954 (T-737 platform-pin move / F-20 follow-up): the JSON-Schema
/// error-flattening in <see cref="InMemorySchemaRegistry"/> drops a JsonSchema.Net
/// 9.4.0 applicator "summary" error (e.g. <c>properties</c>) only when a MORE SPECIFIC
/// keyword failure exists strictly BELOW it in the schema (its evaluation path, not its
/// instance location). Two keywords declared at the SAME schema location that both fail
/// on the same instance value are independent siblings — neither is a summary of the
/// other — and BOTH must survive even though they share a JSON Pointer.
/// </summary>
public sealed class SchemaValidationErrorFlatteningTests
{
    private static InMemorySchemaRegistry NewRegistry() => new(TimeProvider.System);

    /// <summary>
    /// Root-first: <c>not</c> and <c>minLength</c> are both declared directly on the root
    /// schema (same evaluation path), and both fail against the same string instance. A
    /// pointer-only filter would see them sharing the root JSON Pointer and wrongly treat
    /// <c>minLength</c>'s failure as license to drop <c>not</c> as a redundant summary.
    /// </summary>
    [Fact]
    public async Task Independent_keyword_failures_at_the_same_instance_location_are_both_reported()
    {
        var registry = NewRegistry();
        var schema = await registry.RegisterAsync("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "string",
              "not": { "type": "string" },
              "minLength": 5
            }
            """);

        var result = await registry.ValidateAsync(schema.Id, Encoding.UTF8.GetBytes("\"abc\""));

        Assert.False(result.IsValid);
        var codes = result.Errors.Select(e => e.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "minLength", "not" }, codes);
    }

    /// <summary>
    /// F-20's actual regression shape: a real applicator-summary error (<c>properties</c>,
    /// reported by JsonSchema.Net 9.4.0 on the object node) alongside its genuinely more
    /// specific child failure (<c>minLength</c> on one property). Only the summary drops.
    /// </summary>
    [Fact]
    public async Task An_applicator_summary_is_dropped_in_favor_of_its_more_specific_child_failure()
    {
        var registry = NewRegistry();
        var schema = await registry.RegisterAsync("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "name": { "type": "string", "minLength": 5 } },
              "additionalProperties": false
            }
            """);

        var candidate = JsonSerializer.SerializeToUtf8Bytes(new { name = "ab" });
        var result = await registry.ValidateAsync(schema.Id, candidate);

        Assert.False(result.IsValid);
        var codes = result.Errors.Select(e => e.Code).ToArray();
        Assert.Equal(new[] { "minLength" }, codes);
    }
}
