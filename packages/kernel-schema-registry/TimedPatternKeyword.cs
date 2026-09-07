using System.Text.Json;
using System.Text.RegularExpressions;

using Json.Schema;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Drop-in replacement for JsonSchema.Net's built-in <c>pattern</c> keyword handler
/// that compiles each schema-<c>pattern</c> regex with an <i>explicit, per-pattern</i>
/// match-timeout — the ADR 0055 INV-S2 (c) ReDoS control.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> JsonSchema.Net 9.2.0's built-in <c>pattern</c> handler
/// compiles the user's regex with <c>RegexOptions.Compiled | RegexOptions.ECMAScript</c>
/// and an <i>infinite</i> match-timeout, and the library exposes no per-pattern timeout
/// hook. A catastrophic backtracking pattern (e.g. <c>^(a+)+$</c>) against a crafted
/// ~40-byte input therefore pins a core indefinitely (measured: 84&#160;s and climbing).
/// The earlier process-global default-match-timeout approach (the retired
/// <c>SchemaRegistryReDoSFloor</c> module initializer) could not work from a library:
/// .NET caches <c>Regex.s_defaultMatchTimeout</c> at the <c>Regex</c> type's static
/// initializer, before any library assembly loads, so a set-after-static-init slot value
/// is read too late. Swapping the keyword handler itself is the only sound lever, and it
/// bounds the match at the source: the regex is constructed <i>with</i> the timeout.
/// </para>
/// <para>
/// <b>Semantic identity (sec-eng evidence).</b> Match results are identical to the
/// built-in handler for every non-catastrophic input — same ECMA-262 (<c>RegexOptions.ECMAScript</c>)
/// dialect required by JSON Schema <c>pattern</c>, same "unanchored search" semantics
/// (<see cref="Regex.IsMatch(string)"/>, not a full-string match), same ignore-when-not-a-string
/// behavior. The <i>only</i> observable difference is that a catastrophic backtrack now
/// throws <see cref="RegexMatchTimeoutException"/> instead of running unbounded; the
/// registry's fail-closed catch turns that into an invalid result. No legitimate
/// single-property match approaches the budget (real matches complete in microseconds;
/// the budget is ~1000× headroom), so there are no false rejects.
/// </para>
/// <para>
/// <b>Why <c>RegexOptions.Compiled</c> is dropped.</b> <c>Compiled</c> is a
/// performance-only flag — it emits and JITs IL for the pattern and never changes match
/// results — so dropping it preserves semantics. It is dropped deliberately for two
/// security reasons: (1) JIT-compiling an attacker-controlled pattern is itself an
/// unbounded cost that <see cref="Regex"/>'s <c>matchTimeout</c> does <i>not</i> bound
/// (the timeout governs matching, not construction); the interpreted engine has no such
/// compile step. (2) The interpreted engine honors a tight match-timeout cleanly on the
/// catastrophic-backtrack path (verified: aborts at ~200&#160;ms). The pattern's
/// <see cref="Regex"/> is built once per registered schema and cached, so the steady-state
/// cost of interpreting is paid against a per-schema one-time construction, not per
/// validation.
/// </para>
/// </remarks>
internal sealed class TimedPatternKeyword : IKeywordHandler
{
    private readonly TimeSpan _matchTimeout;

    /// <summary>
    /// Creates a <c>pattern</c> handler that compiles every schema regex with the given
    /// per-pattern <paramref name="matchTimeout"/>.
    /// </summary>
    internal TimedPatternKeyword(TimeSpan matchTimeout) => _matchTimeout = matchTimeout;

    /// <inheritdoc />
    public string Name => "pattern";

    /// <summary>
    /// Schema-load-time hook. Compiles the <c>pattern</c> value into a
    /// <see cref="Regex"/> carrying the explicit match-timeout; the result is cached on
    /// the keyword's <c>KeywordData.Value</c> and reused on every evaluation.
    /// </summary>
    public object? ValidateKeywordValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? new Regex(value.GetString()!, RegexOptions.ECMAScript, _matchTimeout)
            : throw new ArgumentException("The `pattern` keyword value must be a string.");

    /// <inheritdoc />
    public KeywordEvaluation Evaluate(KeywordData data, EvaluationContext context)
    {
        // pattern applies only to string instances; for any other type the keyword is
        // an inert annotation (mirrors the built-in handler).
        if (context.Instance.ValueKind != JsonValueKind.String)
        {
            return KeywordEvaluation.Ignore;
        }

        // A catastrophic backtrack throws RegexMatchTimeoutException here; it propagates
        // out of Evaluate and is caught fail-closed in InMemorySchemaRegistry.ValidateAsync.
        var isMatch = ((Regex)data.Value!).IsMatch(context.Instance.GetString()!);

        return new KeywordEvaluation
        {
            Keyword = Name,
            IsValid = isMatch,
            ContributesToValidation = true,
            Error = isMatch ? null : "The string value did not match the required pattern.",
        };
    }

    /// <inheritdoc />
    public void BuildSubschemas(KeywordData data, BuildContext context)
    {
        // pattern has no subschemas.
    }
}
