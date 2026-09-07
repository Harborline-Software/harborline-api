using Json.Schema;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Builds the JSON Schema draft 2020-12 dialect used by
/// <see cref="InMemorySchemaRegistry"/>, with the standard <c>pattern</c> keyword
/// replaced by <see cref="TimedPatternKeyword"/> so every schema-<c>pattern</c> regex
/// is constructed with an explicit per-pattern match-timeout (ADR 0055 INV-S2 (c)).
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction.</b> The standard draft 2020-12 keyword set is sourced from the
/// public <see cref="Vocabulary"/> statics (the union of the seven draft 2020-12
/// vocabularies, de-duplicated by keyword name) — a stable public path, not reflection
/// into the library's internal default dialect. The built-in <c>pattern</c> handler is
/// filtered out and <see cref="TimedPatternKeyword"/> appended in its place.
/// </para>
/// <para>
/// <b>Both levers are required.</b> <see cref="BuildOptions.Dialect"/> alone is honored
/// only for schemas with no <c>$schema</c> keyword; a schema that declares
/// <c>"$schema": "https://json-schema.org/draft/2020-12/schema"</c> (as every fleet
/// fixture does) resolves its dialect through the dialect registry, bypassing the
/// <c>Dialect</c> property. So the timed dialect is ALSO registered under the draft
/// 2020-12 id in a custom <see cref="DialectRegistry"/> supplied via
/// <see cref="BuildOptions.DialectRegistry"/>. Scoping the swap to a per-build
/// <see cref="BuildOptions"/> means no process-global dialect mutation — concurrent
/// consumers using the library's default dialect are unaffected.
/// </para>
/// <para>
/// A schema declaring a <i>foreign</i> dialect (e.g. draft-07) resolves against neither
/// lever and fails the build, surfacing as <c>InvalidSchemaException</c> in
/// <see cref="InMemorySchemaRegistry.RegisterAsync"/> — the registry pins to draft
/// 2020-12 (see its csproj description), so fail-closed on a foreign dialect is correct.
/// </para>
/// </remarks>
internal static class SchemaRegistryDialect
{
    /// <summary>
    /// Per-pattern regex match-timeout applied to every schema <c>pattern</c>. A
    /// legitimate single-property match completes in microseconds, so 200&#160;ms is
    /// ~1000× headroom (zero false-positive risk on real schemas) while still aborting a
    /// catastrophic backtrack before it burns meaningful CPU.
    /// </summary>
    internal static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>The draft 2020-12 dialect id every fleet schema declares (or defaults to).</summary>
    private static readonly Uri Draft202012Id = new("https://json-schema.org/draft/2020-12/schema");

    /// <summary>
    /// Shared <see cref="BuildOptions"/> wiring the timed draft 2020-12 dialect, passed to
    /// <c>JsonSchema.FromText</c> so the timed <c>pattern</c> handler is used for both
    /// <c>$schema</c>-declared and bare schemas. Built once and reused (the dialect and
    /// its handlers are immutable and thread-safe to share).
    /// </summary>
    internal static BuildOptions TimedBuildOptions { get; } = BuildTimedBuildOptions();

    private static BuildOptions BuildTimedBuildOptions()
    {
        var standardKeywords = new[]
            {
                Vocabulary.Draft202012_Core,
                Vocabulary.Draft202012_Applicator,
                Vocabulary.Draft202012_Validation,
                Vocabulary.Draft202012_MetaData,
                Vocabulary.Draft202012_FormatAnnotation,
                Vocabulary.Draft202012_Content,
                Vocabulary.Draft202012_Unevaluated,
            }
            .SelectMany(v => v.Keywords)
            .GroupBy(k => k.Name)
            .Select(g => g.First());

        var timedKeywords = standardKeywords
            .Where(h => h.Name != "pattern")
            .Append((IKeywordHandler)new TimedPatternKeyword(PatternMatchTimeout))
            .ToList();

        var dialect = new Dialect(timedKeywords) { Id = Draft202012Id };

        var registry = new DialectRegistry();
        registry.Register(dialect);

        return new BuildOptions
        {
            Dialect = dialect,
            DialectRegistry = registry,
        };
    }
}
