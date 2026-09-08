using System.Collections.Concurrent;

using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Foundation.Documents.Issuance;

/// <summary>
/// The runtime store of published <see cref="TemplateDefinition"/>s the render pipeline resolves (#111
/// §2.1) — the seam between the pack seed projector (which projects an ACTIVE pack's
/// <c>TemplateDefinition</c> content into here) and the issuance/render path (which resolves a template by
/// key). Version selection reuses the S-8 monotonic <see cref="PackVersion"/> comparator (highest published
/// version wins), the same "which version wins offline" comparator the rule registry uses.
/// </summary>
public interface IDocumentTemplateRegistry
{
    /// <summary>Registers (or upgrades to) a published template version. A lower version than the current is ignored (S-8, no downgrade).</summary>
    void Publish(TemplateDefinition template);

    /// <summary>Removes a pinned published version; true when one was present (pack replacement retraction).</summary>
    bool Remove(string key, string version);

    /// <summary>Resolves the latest published template for <paramref name="key"/>, or null.</summary>
    TemplateDefinition? Resolve(string key);

    /// <summary>Resolves a specific pinned version, or null.</summary>
    TemplateDefinition? Resolve(string key, string version);
}

/// <summary>
/// An in-memory <see cref="IDocumentTemplateRegistry"/> (the cluster default; the node overrides with a
/// durable store). Keeps the highest published version per key under the S-8 no-downgrade rule.
/// </summary>
public sealed class InMemoryDocumentTemplateRegistry : IDocumentTemplateRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TemplateDefinition>> _byKey =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Publish(TemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var versions = _byKey.GetOrAdd(template.Key, _ => new ConcurrentDictionary<string, TemplateDefinition>(StringComparer.Ordinal));
        versions[template.Version] = template;
    }

    /// <inheritdoc />
    public bool Remove(string key, string version)
        => _byKey.TryGetValue(key, out var versions) && versions.TryRemove(version, out _);

    /// <inheritdoc />
    public TemplateDefinition? Resolve(string key)
    {
        if (!_byKey.TryGetValue(key, out var versions) || versions.IsEmpty)
        {
            return null;
        }

        TemplateDefinition? latest = null;
        foreach (var candidate in versions.Values)
        {
            if (latest is null || PackVersion.Compare(candidate.Version, latest.Version) > 0)
            {
                latest = candidate;
            }
        }

        return latest;
    }

    /// <inheritdoc />
    public TemplateDefinition? Resolve(string key, string version)
        => _byKey.TryGetValue(key, out var versions) && versions.TryGetValue(version, out var template)
            ? template
            : null;
}
