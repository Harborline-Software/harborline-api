using System.Collections.Concurrent;
using System.Text.Json;

using Harborline.Api.Foundation.Definitions;
using Harborline.Foundation.RuleEngine.Standings;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>Reads and updates the host projection of installed immutable standing-rule versions.</summary>
public interface IStandingRuleDefinitionStore
{
    /// <summary>Registers an immutable pack-projected rule version; an exact replay has no effect.</summary>
    ValueTask RegisterAsync(StandingRuleDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Returns the rule at the requested immutable identity, or no value when it is not projected.</summary>
    ValueTask<StandingRuleDefinition?> GetAsync(string ruleId, string ruleVersion, CancellationToken cancellationToken = default);

    /// <summary>Removes a projected rule version and reports whether it was present.</summary>
    ValueTask<bool> RemoveAsync(string ruleId, string ruleVersion, CancellationToken cancellationToken = default);

    /// <summary>Lists the current projected rules in stable rule-id and version order.</summary>
    IAsyncEnumerable<StandingRuleDefinition> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>In-memory transactional projection of standing-rule content installed from packs.</summary>
public sealed class InMemoryStandingRuleDefinitionStore : IStandingRuleDefinitionStore, IPackProjectionParticipant
{
    private ConcurrentDictionary<(string RuleId, string Version), StandingRuleDefinition> _rows = [];
    private readonly IRestrictingDefinitionKindValidator _restrictingKinds;

    /// <summary>Creates the projection over the canonical restricting-kind validator.</summary>
    public InMemoryStandingRuleDefinitionStore(IRestrictingDefinitionKindValidator? restrictingKinds = null)
        => _restrictingKinds = restrictingKinds ?? RestrictingDefinitionKindValidator.Shared;

    /// <inheritdoc />
    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var before = _rows;
        _rows = new ConcurrentDictionary<(string RuleId, string Version), StandingRuleDefinition>(before);
        return () => _rows = before;
    });

    /// <inheritdoc />
    public ValueTask RegisterAsync(StandingRuleDefinition definition, CancellationToken cancellationToken = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(cancellationToken);
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        _restrictingKinds.EnsureKnown(
            RestrictingDefinitionKindFamily.RuleAction,
            definition.RuleId,
            definition.Predicate.Action.ToString(),
            nestedDefinitionId: definition.Predicate.Id);
        var key = (definition.RuleId, definition.RuleVersion);
        if (_rows.TryAdd(key, definition)) return ValueTask.CompletedTask;

        if (!string.Equals(JsonSerializer.Serialize(_rows[key]), JsonSerializer.Serialize(definition), StringComparison.Ordinal))
            throw new InvalidOperationException($"Standing rule '{definition.RuleId}' version '{definition.RuleVersion}' already has different content.");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<StandingRuleDefinition?> GetAsync(string ruleId, string ruleVersion, CancellationToken cancellationToken = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _rows.TryGetValue((ruleId, ruleVersion), out var definition);
        return ValueTask.FromResult(definition);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string ruleId, string ruleVersion, CancellationToken cancellationToken = default)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_rows.TryRemove((ruleId, ruleVersion), out _));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StandingRuleDefinition> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StandingRuleDefinition[] snapshot;
        using (PackProjectionActivationBarrier.Read(cancellationToken))
            snapshot = _rows.Values.OrderBy(definition => definition.RuleId, StringComparer.Ordinal)
                .ThenBy(definition => definition.RuleVersion, StringComparer.Ordinal).ToArray();
        foreach (var definition in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return definition;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// Reads installed standing-rule content. The platform <see cref="StandingRuleDefinition"/> has no JSON constructor, so
/// pack content binds to its serialized shape here and is then validated by the platform constructor.
/// </summary>
public static class StandingRuleDefinitionJson
{
    private sealed record Wire(
        string RuleId,
        string RuleVersion,
        WireStanding Standing,
        string RecordType,
        string[] InputFields,
        Harborline.Contracts.Forms.RuleDefinition Predicate);

    private sealed record WireStanding(string Name);

    /// <summary>
    /// Parses one standing rule; throws <see cref="JsonException"/> on malformed JSON and <see cref="ArgumentException"/>
    /// when the platform refuses the definition. Returns no value for a JSON null.
    /// </summary>
    public static StandingRuleDefinition? Deserialize(string json, JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<Wire>(json, options);
        if (wire is null) return null;
        if (wire.Standing is null || wire.InputFields is null || wire.Predicate is null)
            throw new JsonException("A standing rule requires standing, inputFields and predicate.");
        return new StandingRuleDefinition(
            wire.RuleId, wire.RuleVersion, new StandingReference(wire.Standing.Name), wire.RecordType, wire.InputFields, wire.Predicate);
    }
}
