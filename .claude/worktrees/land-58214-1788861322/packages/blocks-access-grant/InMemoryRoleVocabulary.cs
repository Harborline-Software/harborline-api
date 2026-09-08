using System.Collections.Concurrent;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// An in-memory role vocabulary: the sealed platform seed plus the constructor-seeded and
/// pack-installed domain entries. Only <see cref="IRoleVocabularyStore"/> mutates it, and never a
/// sealed platform entry.
/// </summary>
public sealed class InMemoryRoleVocabulary : IRoleVocabularyStore
{
    private static readonly RoleOwner PlatformOwner =
        new(RoleOwnerKind.Platform, RoleVocabularies.Platform);

    private readonly ConcurrentDictionary<RoleReference, RoleDefinition> _definitions;

    /// <summary>Creates a vocabulary containing only the sealed platform seed.</summary>
    public InMemoryRoleVocabulary()
        : this(Array.Empty<RoleDefinition>())
    {
    }

    /// <summary>Creates a vocabulary from the sealed platform seed plus installed domain entries.</summary>
    public InMemoryRoleVocabulary(IEnumerable<RoleDefinition> installedDefinitions)
    {
        ArgumentNullException.ThrowIfNull(installedDefinitions);
        var definitions = PlatformDefinitions()
            .Concat(installedDefinitions)
            .ToArray();
        var duplicate = definitions.GroupBy(definition => definition.Role)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Role '{duplicate.Key}' is installed more than once.",
                nameof(installedDefinitions));
        }

        _definitions = new ConcurrentDictionary<RoleReference, RoleDefinition>(
            definitions.Select(definition =>
                new KeyValuePair<RoleReference, RoleDefinition>(definition.Role, definition)));
    }

    /// <inheritdoc />
    public ValueTask InstallAsync(RoleDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ct.ThrowIfCancellationRequested();
        RefuseSealed(definition.Role);
        var stored = _definitions.GetOrAdd(definition.Role, definition);
        if (!stored.Equals(definition))
        {
            throw new InvalidOperationException(
                $"Role '{definition.Role}' is already installed with a different definition.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(RoleReference role, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RefuseSealed(role);
        return ValueTask.FromResult(_definitions.TryRemove(role, out _));
    }

    private void RefuseSealed(RoleReference role)
    {
        if (role.Vocabulary == RoleVocabularies.Platform
            || (_definitions.TryGetValue(role, out var existing) && existing.IsSealed))
        {
            throw new InvalidOperationException($"Role '{role}' is a sealed platform entry.");
        }
    }

    /// <inheritdoc />
    public ValueTask<RoleDefinition?> ResolveAsync(
        RoleReference role,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _definitions.TryGetValue(role, out var definition) ? definition : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RoleDefinition>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<RoleDefinition>>(_definitions.Values
            .OrderBy(definition => definition.Role.Vocabulary, StringComparer.Ordinal)
            .ThenBy(definition => definition.Role.Name, StringComparer.Ordinal)
            .ToArray());
    }

    private static IEnumerable<RoleDefinition> PlatformDefinitions()
    {
        yield return new RoleDefinition(
            new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896101")),
            RoleReference.Administrator,
            "Administrator",
            PlatformOwner,
            IsSealed: true);
        yield return new RoleDefinition(
            new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896102")),
            RoleReference.Auditor,
            "Auditor",
            PlatformOwner,
            IsSealed: true);
    }
}
