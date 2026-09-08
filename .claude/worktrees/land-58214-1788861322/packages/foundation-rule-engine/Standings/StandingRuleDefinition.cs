using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine.Compilation;

namespace Harborline.Api.Foundation.RuleEngine.Standings;

/// <summary>A domain-declared position computed from one record type's own fields.</summary>
public readonly record struct StandingReference
{
    /// <summary>Creates a standing name. Standings are facts, not role references.</summary>
    [JsonConstructor]
    public StandingReference(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The domain-declared standing name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// An ordinary versioned definition that computes a standing from declared record fields.
/// Its predicate reuses the rule engine's closed <c>shipyard-jsonlogic/v1</c>
/// <see cref="RuleDefinition"/> representation.
/// </summary>
public sealed record StandingRuleDefinition
{
    /// <summary>Creates and validates a standing rule definition.</summary>
    [JsonConstructor]
    public StandingRuleDefinition(
        string RuleId,
        string RuleVersion,
        StandingReference Standing,
        string RecordType,
        IReadOnlyList<string> InputFields,
        RuleDefinition Predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RuleVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(RecordType);
        ArgumentNullException.ThrowIfNull(InputFields);
        ArgumentNullException.ThrowIfNull(Predicate);
        if (Standing == default)
            throw new ArgumentException("A standing name is required.", nameof(Standing));
        if (InputFields.Count == 0 || InputFields.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty input field is required.", nameof(InputFields));
        if (InputFields.Distinct(StringComparer.Ordinal).Count() != InputFields.Count)
            throw new ArgumentException("Input fields must be unique.", nameof(InputFields));
        if (Predicate.Tier != RuleTier.JsonLogic
            || Predicate.Action != RuleActionKind.Validate
            || Predicate.Scope != RuleScope.Schema)
            throw new ArgumentException(
                "A standing predicate must be a schema-scoped shipyard-jsonlogic/v1 validation rule.",
                nameof(Predicate));
        if (!string.Equals(Predicate.Id, RuleId, StringComparison.Ordinal))
            throw new ArgumentException("The predicate id must equal the standing rule id.", nameof(Predicate));
        if (!string.Equals(Predicate.Envelope.Version, RuleVersion, StringComparison.Ordinal))
            throw new ArgumentException("The predicate version must equal the standing rule version.", nameof(Predicate));

        var lowered = ScopeGrammar.Lower(
            Predicate.Expression,
            new LowerContext(RuleScope.Schema, string.Empty, null),
            RuleId);
        var references = ScopeGrammar.ExtractRefs(lowered, RuleId);
        if (references.Any(reference => reference is not FieldRef))
            throw new ArgumentException("Standing predicates may read only top-level record fields.", nameof(Predicate));
        var referencedFields = references.OfType<FieldRef>()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!referencedFields.SetEquals(InputFields))
            throw new ArgumentException(
                "Input fields must exactly name every record field read by the predicate.",
                nameof(InputFields));

        this.RuleId = RuleId;
        this.RuleVersion = RuleVersion;
        this.Standing = Standing;
        this.RecordType = RecordType;
        this.InputFields = InputFields.ToArray();
        this.Predicate = Predicate;
    }

    /// <summary>The stable rule identity used by evidence.</summary>
    public string RuleId { get; }

    /// <summary>The installed rule version used by evidence.</summary>
    public string RuleVersion { get; }

    /// <summary>The standing carried when the predicate passes.</summary>
    public StandingReference Standing { get; }

    /// <summary>The record type on which this rule is declared.</summary>
    public string RecordType { get; }

    /// <summary>The complete, ordered set of record fields available to the predicate.</summary>
    public IReadOnlyList<string> InputFields { get; }

    /// <summary>The deterministic closed JsonLogic predicate reused from the shared rule engine.</summary>
    public RuleDefinition Predicate { get; }
}
