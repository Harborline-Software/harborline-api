namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Register-time resource bounds for <see cref="InMemorySchemaRegistry"/>
/// (ADR 0055 INV-S2 (a) + (b)). These are the cheap, deterministic pre-rejects
/// applied when a schema is <see cref="ISchemaRegistry.RegisterAsync"/>-ed; a
/// schema that exceeds them fails with a typed
/// <see cref="InvalidSchemaException"/> at register-time rather than surfacing
/// as a 500 on the first payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does NOT cover (INV-S2 (c) — ReDoS).</b> The catastrophic-regex
/// control is the explicit per-pattern <c>Regex</c> match-timeout that
/// <see cref="TimedPatternKeyword"/> compiles into every schema <c>pattern</c>
/// (wired via <see cref="SchemaRegistryDialect"/>) plus the
/// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> catch
/// in <see cref="InMemorySchemaRegistry.ValidateAsync"/>. A size / depth cap
/// cannot bound a catastrophic <c>pattern</c> match — a 40-byte schema can pin a
/// core — so (c) lives at the regex layer, not here.
/// </para>
/// <para>
/// <b>Defaults preserve current behavior.</b> <see cref="MaxNestingDepth"/>
/// defaults to 64, which equals <c>System.Text.Json</c>'s own default reader
/// depth — so wiring this option changes nothing for existing fleet schemas
/// (all KB-scale and shallow); it only makes the previously-implicit depth bound
/// explicit and tunable.
/// </para>
/// </remarks>
public sealed class SchemaRegistryOptions
{
    /// <summary>Default schema-text ceiling: 256 KiB of canonical UTF-8 bytes.</summary>
    public const int DefaultMaxSchemaBytes = 256 * 1024;

    /// <summary>Default structural nesting-depth ceiling: 64 (matches the System.Text.Json reader default).</summary>
    public const int DefaultMaxNestingDepth = 64;

    /// <summary>
    /// Maximum size, in canonical UTF-8 bytes, of a registered schema document
    /// (INV-S2 (a)). A larger schema is rejected with
    /// <see cref="InvalidSchemaException"/> before
    /// <c>JsonSchema.FromText</c> runs.
    /// </summary>
    public int MaxSchemaBytes { get; init; } = DefaultMaxSchemaBytes;

    /// <summary>
    /// Maximum structural nesting depth of a registered schema document
    /// (INV-S2 (b), the depth half). Enforced by parsing the schema with a
    /// bounded <see cref="System.Text.Json.JsonDocumentOptions.MaxDepth"/>: a
    /// document nested deeper than this is rejected with
    /// <see cref="InvalidSchemaException"/>. This bounds the evaluator's
    /// structural recursion at register-time; <c>$ref</c>-expansion depth and
    /// recursive-<c>$ref</c> evaluation are bounded at evaluate-time by
    /// JsonSchema.Net's reference handling and the engine-side wall-clock budget.
    /// </summary>
    public int MaxNestingDepth { get; init; } = DefaultMaxNestingDepth;

    /// <summary>Shared default-bounds instance used when no options are supplied.</summary>
    public static SchemaRegistryOptions Default { get; } = new();
}
