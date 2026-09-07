using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Policy;

/// <summary>
/// In-memory <see cref="IPolicyRegistry"/> keyed by <c>(System, Code)</c> (ordinal).
/// Seeded with <see cref="PredefinedPolicyBindings.All"/> plus any custom bindings the
/// host supplies; a later binding for the same key overrides an earlier one.
/// </summary>
public sealed class InMemoryPolicyRegistry : IPolicyRegistry
{
    private readonly IReadOnlyDictionary<string, PolicyBinding> _byKey;

    /// <summary>Construct a registry seeded with the predefined bindings only.</summary>
    public InMemoryPolicyRegistry() : this(includePredefined: true, custom: null) { }

    /// <summary>
    /// Construct a registry. When <paramref name="includePredefined"/> is true the
    /// <see cref="PredefinedPolicyBindings.All"/> seed is loaded first; any
    /// <paramref name="custom"/> binding with the same <c>(System, Code)</c> key overrides it.
    /// </summary>
    public InMemoryPolicyRegistry(
        bool includePredefined,
        IEnumerable<PolicyBinding>? custom,
        IRestrictingDefinitionKindValidator? restrictingKinds = null)
    {
        var kinds = restrictingKinds ?? RestrictingDefinitionKindValidator.Shared;
        var map = new Dictionary<string, PolicyBinding>(StringComparer.Ordinal);
        if (includePredefined)
        {
            foreach (var b in PredefinedPolicyBindings.All)
            {
                EnsureKnownKinds(b, kinds);
                map[Key(b.Tag)] = b;
            }
        }
        if (custom is not null)
        {
            foreach (var b in custom)
            {
                EnsureKnownKinds(b, kinds);
                map[Key(b.Tag)] = b;
            }
        }
        _byKey = map;
    }

    /// <inheritdoc />
    public PolicyBinding? Resolve(Tag tag)
        => _byKey.TryGetValue(Key(tag), out var binding) ? binding : null;

    /// <summary>
    /// The lookup key. Every accepted spelling of the predefined data-classification system
    /// (ticket 260 rename window) folds onto the canonical one, so a tag in the renamed system
    /// resolves to the SAME predefined binding as the retired one — one seam for every registry
    /// consumer (the resolver, the admission validator, the Store/Read/Export PEPs). Folding is
    /// keyed off the RAW system: a whitespace-dirty tag is not an accepted member and still fails
    /// closed at admission (2b) rather than binding on the strength of a trim.
    /// </summary>
    internal static string Key(Tag tag)
        => (PredefinedPolicyBindings.IsDataClassificationSystem(tag.System)
            ? PredefinedPolicyBindings.DataClassificationSystem
            : tag.System) + "|" + tag.Code;

    private static void EnsureKnownKinds(PolicyBinding binding, IRestrictingDefinitionKindValidator kinds)
    {
        foreach (var effect in binding.Effects)
        {
            kinds.EnsureKnown(
                RestrictingDefinitionKindFamily.Policy,
                binding.Tag.Code,
                effect.Kind.ToString());
        }

        foreach (var required in binding.Class?.RequiredEffects ?? Array.Empty<RequiredEffect>())
        {
            kinds.EnsureKnown(
                RestrictingDefinitionKindFamily.Policy,
                binding.Tag.Code,
                required.Kind.ToString());
        }
    }
}
