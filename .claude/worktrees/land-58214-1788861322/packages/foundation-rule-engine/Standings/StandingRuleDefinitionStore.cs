using System.Collections.Concurrent;
using System.Text.Json;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.RuleEngine.Standings;

/// <summary>The ordinary installed-content row carrying one immutable standing-rule version.</summary>
public sealed record StandingDefinitionRow(StandingRuleDefinition Definition);

/// <summary>The ordinary definition-row store populated by installed pack content.</summary>
public interface IStandingRuleDefinitionStore
{
    /// <summary>Registers an immutable rule-version row; exact replay is idempotent.</summary>
    ValueTask RegisterAsync(
        StandingRuleDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a particular rule-version row.</summary>
    ValueTask<StandingRuleDefinition?> GetAsync(
        string ruleId,
        string ruleVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a rule-version row; true when one was present (pack replacement retraction).</summary>
    ValueTask<bool> RemoveAsync(
        string ruleId,
        string ruleVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Lists all installed rows in stable identity/version order.</summary>
    IAsyncEnumerable<StandingRuleDefinition> ListAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>In-memory reference store for installed standing-rule definition rows.</summary>
public sealed class InMemoryStandingRuleDefinitionStore : IStandingRuleDefinitionStore
{
    private readonly ConcurrentDictionary<(string RuleId, string Version), StandingDefinitionRow> _rows = [];
    private readonly IRestrictingDefinitionKindValidator _restrictingKinds;

    /// <summary>Constructs the store over the canonical restricting-kind validator.</summary>
    public InMemoryStandingRuleDefinitionStore(IRestrictingDefinitionKindValidator? restrictingKinds = null)
        => _restrictingKinds = restrictingKinds ?? RestrictingDefinitionKindValidator.Shared;

    /// <inheritdoc />
    public ValueTask RegisterAsync(
        StandingRuleDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        _restrictingKinds.EnsureKnown(
            RestrictingDefinitionKindFamily.RuleAction,
            definition.RuleId,
            definition.Predicate.Action.ToString(),
            nestedDefinitionId: definition.Predicate.Id);
        var key = (definition.RuleId, definition.RuleVersion);
        if (_rows.TryAdd(key, new StandingDefinitionRow(definition)))
            return ValueTask.CompletedTask;

        var existing = _rows[key].Definition;
        if (!string.Equals(
                JsonSerializer.Serialize(existing),
                JsonSerializer.Serialize(definition),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Standing rule '{definition.RuleId}' version '{definition.RuleVersion}' already has different content.");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<StandingRuleDefinition?> GetAsync(
        string ruleId,
        string ruleVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rows.TryGetValue((ruleId, ruleVersion), out var row);
        return ValueTask.FromResult(row?.Definition);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(
        string ruleId,
        string ruleVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_rows.TryRemove((ruleId, ruleVersion), out _));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StandingRuleDefinition> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows.Values
            .OrderBy(row => row.Definition.RuleId, StringComparer.Ordinal)
            .ThenBy(row => row.Definition.RuleVersion, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row.Definition;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
