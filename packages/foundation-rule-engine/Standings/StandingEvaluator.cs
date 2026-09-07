using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.RuleEngine.Standings;

/// <summary>A record-shaped input to standing computation.</summary>
public sealed record StandingRecord(
    string RecordType,
    IReadOnlyDictionary<string, JsonNode?> FieldValues);

/// <summary>The deciding inputs and versioned rule behind one standing verdict.</summary>
public sealed record StandingRuleEvidence(
    string RuleId,
    string RuleVersion,
    StandingReference Standing,
    bool CarriesStanding,
    IReadOnlyDictionary<string, JsonNode?> InputFieldValues,
    DateTimeOffset Instant,
    string? RefusalCode = null);

/// <summary>A deterministic set of carried standings plus evidence for every applicable rule.</summary>
public sealed record StandingEvaluationResult(
    IReadOnlyList<StandingReference> Standings,
    IReadOnlyList<StandingRuleEvidence> Evidence);

/// <summary>
/// Computes record standings without accepting a principal, role, grant, delegation, or session input.
/// This is the seam the point-of-use authorization gate can call later.
/// </summary>
public sealed class StandingEvaluator
{
    private readonly RuleEngineLimits _limits;

    /// <summary>Creates an evaluator using the shared rule engine's default deterministic limits.</summary>
    public StandingEvaluator(RuleEngineLimits? limits = null) =>
        _limits = limits ?? RuleEngineLimits.Default;

    /// <summary>Evaluates all rules declared on the record's type at the supplied act instant.</summary>
    public StandingEvaluationResult Evaluate(
        StandingRecord record,
        DateTimeOffset instant,
        IEnumerable<StandingRuleDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RecordType);
        ArgumentNullException.ThrowIfNull(record.FieldValues);
        ArgumentNullException.ThrowIfNull(definitions);

        var evidence = new List<StandingRuleEvidence>();
        var carried = new HashSet<StandingReference>();
        foreach (var definition in definitions
            .Where(rule => string.Equals(rule.RecordType, record.RecordType, StringComparison.Ordinal))
            .OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
            .ThenBy(rule => rule.RuleVersion, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var field in definition.InputFields)
            {
                record.FieldValues.TryGetValue(field, out var value);
                inputs.Add(field, value?.DeepClone());
            }

            var verdict = new GuardEvaluator(_limits)
                .EvaluateGuardAt(definition.Predicate, inputs, instant, cancellationToken);
            if (verdict.Ok)
                carried.Add(definition.Standing);
            evidence.Add(new StandingRuleEvidence(
                definition.RuleId,
                definition.RuleVersion,
                definition.Standing,
                verdict.Ok,
                inputs,
                instant,
                verdict.Error?.Code));
        }

        return new StandingEvaluationResult(
            carried.OrderBy(standing => standing.Name, StringComparer.Ordinal).ToArray(),
            evidence);
    }
}
