using System.Collections.Concurrent;
using System.Text.Json;

namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>Thread-safe, in-memory reference implementation of <see cref="IViewDefinitionRegistry"/>.</summary>
public sealed class InMemoryViewDefinitionRegistry : IViewDefinitionRegistry
{
    private const string PinnedTupleConflict = "view_definition.pinned_tuple_conflict";

    private readonly IViewDefinitionDescriptorRegistry _descriptors;
    private readonly IViewDefinitionCanonicalizer _canonicalizer;
    private readonly ConcurrentDictionary<(string Tenant, string Key, string Version), ViewDefinition> _definitions = new();

    /// <summary>Initializes the registry with engine-owned descriptor admission and pass-through canonicalization.</summary>
    /// <param name="descriptors">The host-supplied view descriptor registry.</param>
    public InMemoryViewDefinitionRegistry(IViewDefinitionDescriptorRegistry descriptors)
        : this(descriptors, new PassThroughViewDefinitionCanonicalizer())
    {
    }

    /// <summary>Initializes the registry with engine-owned descriptor admission and explicit canonicalization.</summary>
    /// <param name="descriptors">The host-supplied view descriptor registry.</param>
    /// <param name="canonicalizer">The canonicalizer applied before admission and persistence.</param>
    public InMemoryViewDefinitionRegistry(
        IViewDefinitionDescriptorRegistry descriptors,
        IViewDefinitionCanonicalizer canonicalizer)
    {
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
    }

    /// <inheritdoc />
    public async ValueTask<ViewDefinition> RegisterAsync(
        ViewDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var canonical = _canonicalizer.Canonicalize(definition)
            ?? throw new InvalidOperationException("The view definition canonicalizer returned null.");
        Validate(canonical);
        await _descriptors.AdmitAsync(canonical, cancellationToken).ConfigureAwait(false);
        if (canonical.ShapeRoles is { HasMappings: true } shapeRoles)
        {
            var recordType = await _descriptors
                .DescribeRecordTypeAsync(canonical, cancellationToken)
                .ConfigureAwait(false);
            ShapeRoleMappingValidator.Validate(shapeRoles, recordType);
        }

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

        throw new ViewDefinitionGovernanceException(PinnedTupleConflict);
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
    public ValueTask<ViewDefinition?> GetDefinitionAsync(
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
        return ValueTask.FromResult<ViewDefinition?>(definition);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ViewDefinition>> ListDefinitionsAsync(
        string tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        cancellationToken.ThrowIfCancellationRequested();
        var heads = _definitions.Values
            .Where(definition => StringComparer.Ordinal.Equals(definition.Tenant, tenant))
            .GroupBy(definition => definition.Key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => OrderNewestFirst([.. group]).Versions[0])
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ViewDefinition>>(heads);
    }

    /// <inheritdoc />
    public ValueTask<ViewDefinitionVersionList?> ListVersionsAsync(
        string tenant,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        var revisions = _definitions.Values
            .Where(definition => StringComparer.Ordinal.Equals(definition.Tenant, tenant)
                && StringComparer.Ordinal.Equals(definition.Key, key))
            .ToList();
        return ValueTask.FromResult(revisions.Count == 0 ? null : OrderNewestFirst(revisions));
    }

    // The pinned ordering rule (ticket 085): semver-descending when EVERY version string of the
    // key is a strict numeric MAJOR.MINOR.PATCH triple, ordinal-string descending otherwise.
    // Version was never validated beyond non-whitespace, and the dictionary records no admission
    // order, so this rule is the only thing making history deterministic across processes.
    private static ViewDefinitionVersionList OrderNewestFirst(List<ViewDefinition> revisions)
    {
        var parsed = new Dictionary<string, (int Major, int Minor, int Patch)>(StringComparer.Ordinal);
        var allSemver = true;
        foreach (var revision in revisions)
        {
            if (TryParseStrictSemver(revision.Version, out var triple))
            {
                parsed[revision.Version] = triple;
            }
            else
            {
                allSemver = false;
                break;
            }
        }

        var ordered = allSemver
            ? revisions.OrderByDescending(revision => parsed[revision.Version].Major)
                .ThenByDescending(revision => parsed[revision.Version].Minor)
                .ThenByDescending(revision => parsed[revision.Version].Patch)
                .ToList()
            : revisions.OrderByDescending(revision => revision.Version, StringComparer.Ordinal).ToList();
        return new ViewDefinitionVersionList(allSemver ? "semver" : "ordinal", ordered);
    }

    private static bool TryParseStrictSemver(string version, out (int Major, int Minor, int Patch) triple)
    {
        triple = default;
        var parts = version.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        triple = (major, minor, patch);
        return true;
    }

    private static void Validate(ViewDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ViewKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        if (definition.SchemaVersion != 1)
        {
            throw new ViewDefinitionGovernanceException("view_definition.schema_version_unsupported");
        }

        if (definition.Parameters.ValueKind != JsonValueKind.Object)
        {
            throw new ViewDefinitionGovernanceException("view_definition.parameters_not_object");
        }
    }

    private static ViewDefinition Detach(ViewDefinition definition) => definition with
    {
        Envelope = definition.Envelope with { Provenance = definition.Provenance.Clone() },
        Parameters = definition.Parameters.Clone(),
    };

    private static bool Equivalent(ViewDefinition left, ViewDefinition right) =>
        left.SchemaVersion == right.SchemaVersion &&
        StringComparer.Ordinal.Equals(left.Tenant, right.Tenant) &&
        StringComparer.Ordinal.Equals(left.Key, right.Key) &&
        StringComparer.Ordinal.Equals(left.Version, right.Version) &&
        left.Envelope.CascadeLayer == right.Envelope.CascadeLayer &&
        RequirementsEqual(left.Envelope.Requires, right.Envelope.Requires) &&
        JsonEqual(left.Provenance, right.Provenance) &&
        StringComparer.Ordinal.Equals(left.ViewKind, right.ViewKind) &&
        StringComparer.Ordinal.Equals(left.Title, right.Title) &&
        Equals(left.ShapeRoles, right.ShapeRoles) &&
        JsonEqual(left.Parameters, right.Parameters);

    private static bool RequirementsEqual(
        IReadOnlyList<Harborline.Api.Foundation.Definitions.DefinitionRequirement> left,
        IReadOnlyList<Harborline.Api.Foundation.Definitions.DefinitionRequirement> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    private static bool JsonEqual(JsonElement left, JsonElement right) =>
        StringComparer.Ordinal.Equals(left.GetRawText(), right.GetRawText());
}
