using System.Collections.Concurrent;
using System.Text.Json;

namespace Harborline.Api.Foundation.ScheduleDefinitions;

/// <summary>Thread-safe, in-memory reference implementation of <see cref="IScheduleDefinitionRegistry"/>.</summary>
public sealed class InMemoryScheduleDefinitionRegistry : IScheduleDefinitionRegistry
{
    private const string PinnedTupleConflict = "schedule_definition.pinned_tuple_conflict";

    private readonly IScheduleDefinitionDescriptorRegistry _descriptors;
    private readonly IScheduleDefinitionCanonicalizer _canonicalizer;
    private readonly ConcurrentDictionary<(string Tenant, string Key, string Version), ScheduleDefinition> _definitions = new();

    /// <summary>Initializes the registry with engine-owned descriptor admission and pass-through canonicalization.</summary>
    /// <param name="descriptors">The host-supplied schedule descriptor registry.</param>
    public InMemoryScheduleDefinitionRegistry(IScheduleDefinitionDescriptorRegistry descriptors)
        : this(descriptors, new PassThroughScheduleDefinitionCanonicalizer())
    {
    }

    /// <summary>Initializes the registry with engine-owned descriptor admission and explicit canonicalization.</summary>
    /// <param name="descriptors">The host-supplied schedule descriptor registry.</param>
    /// <param name="canonicalizer">The canonicalizer applied before admission and persistence.</param>
    public InMemoryScheduleDefinitionRegistry(
        IScheduleDefinitionDescriptorRegistry descriptors,
        IScheduleDefinitionCanonicalizer canonicalizer)
    {
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
    }

    /// <inheritdoc />
    public async ValueTask<ScheduleDefinition> RegisterAsync(
        ScheduleDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var canonical = _canonicalizer.Canonicalize(definition)
            ?? throw new InvalidOperationException("The schedule definition canonicalizer returned null.");
        Validate(canonical);
        await _descriptors.AdmitAsync(canonical, cancellationToken).ConfigureAwait(false);

        var detached = Detach(canonical);
        var tuple = (detached.Tenant, detached.Key, detached.Version);
        if (_definitions.TryAdd(tuple, detached))
        {
            return detached;
        }

        var existing = _definitions[tuple];
        if (Equivalent(existing, detached))
        {
            return existing;
        }

        throw new ScheduleDefinitionGovernanceException(PinnedTupleConflict);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(
        string tenant,
        string key,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_definitions.TryRemove((tenant, key, version), out _));
    }

    /// <inheritdoc />
    public ValueTask<ScheduleDefinition?> GetDefinitionAsync(
        string tenant,
        string key,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();
        _definitions.TryGetValue((tenant, key, version), out var definition);
        return ValueTask.FromResult<ScheduleDefinition?>(definition);
    }

    private static void Validate(ScheduleDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ScheduleKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        if (definition.SchemaVersion != 1)
        {
            throw new ScheduleDefinitionGovernanceException("schedule_definition.schema_version_unsupported");
        }

        if (definition.Body.ValueKind != JsonValueKind.Object)
        {
            throw new ScheduleDefinitionGovernanceException("schedule_definition.body_not_object");
        }
    }

    private static ScheduleDefinition Detach(ScheduleDefinition definition) => definition with
    {
        Envelope = definition.Envelope with { Provenance = definition.Provenance.Clone() },
        Body = definition.Body.Clone(),
    };

    private static bool Equivalent(ScheduleDefinition left, ScheduleDefinition right) =>
        left.SchemaVersion == right.SchemaVersion &&
        StringComparer.Ordinal.Equals(left.Tenant, right.Tenant) &&
        StringComparer.Ordinal.Equals(left.Key, right.Key) &&
        StringComparer.Ordinal.Equals(left.Version, right.Version) &&
        left.Envelope.CascadeLayer == right.Envelope.CascadeLayer &&
        RequirementsEqual(left.Envelope.Requires, right.Envelope.Requires) &&
        JsonEqual(left.Provenance, right.Provenance) &&
        StringComparer.Ordinal.Equals(left.ScheduleKind, right.ScheduleKind) &&
        StringComparer.Ordinal.Equals(left.Title, right.Title) &&
        JsonEqual(left.Body, right.Body);

    private static bool RequirementsEqual(
        IReadOnlyList<Harborline.Api.Foundation.Definitions.DefinitionRequirement> left,
        IReadOnlyList<Harborline.Api.Foundation.Definitions.DefinitionRequirement> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    private static bool JsonEqual(JsonElement left, JsonElement right) =>
        StringComparer.Ordinal.Equals(left.GetRawText(), right.GetRawText());
}
