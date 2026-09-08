using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// A single-level declared dependency on another pack — a bounded slice of the ADR 0129 D3
/// composition DAG (architect fold A9). v1 supports only <b>single-level</b> declared dependencies
/// (a workflow + its forms; a type + its bound forms). The validator FAIL-CLOSED REFUSES a pack
/// whose declared dependency itself declares dependencies (deps-of-deps), rather than silently
/// ignoring the transitive need.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeclaredDependencyKeys"/> records the dependency KEYS that THIS dependency itself
/// declares, as observed by the composer at compose time (it composed over that dependency, so it
/// can see the dependency pack's own <c>Dependencies</c>). An empty list means the dependency is a
/// LEAF (single-level satisfied). A NON-empty list means the dependency has its own dependencies —
/// the single-level assumption is violated and the validator refuses the pack
/// (<c>PACK_VALIDATION_TRANSITIVE_DEPENDENCY</c>). This lets B-1a enforce A9 self-containedly at
/// export time without loading every sibling manifest.
/// </para>
/// <para>
/// The 3-arg constructor is marked <see cref="JsonConstructorAttribute"/> so System.Text.Json can
/// deserialize this record: a pack that actually CARRIES a dependency round-trips through the transport
/// codec (the signed manifest is deserialized at verify), and STJ refuses to pick a constructor for a
/// type with two public constructors unless exactly one is annotated. Without this, any pack declaring a
/// dependency failed to decode → a false <c>not_verified</c> (bug-B1F-dep-ctor).
/// </para>
/// </remarks>
public sealed record PackDependencyRef
{
    /// <summary>The dependency pack's key.</summary>
    public string Key { get; }

    /// <summary>The PINNED version of the dependency (never a mutable id).</summary>
    public string Version { get; }

    /// <summary>The keys the dependency ITSELF declares (empty ⇒ leaf; non-empty ⇒ deps-of-deps, A9).</summary>
    public IReadOnlyList<string> DeclaredDependencyKeys { get; }

    /// <summary>The canonical (JSON-deserialization) constructor.</summary>
    [JsonConstructor]
    public PackDependencyRef(string key, string version, IReadOnlyList<string> declaredDependencyKeys)
    {
        Key = key;
        Version = version;
        DeclaredDependencyKeys = declaredDependencyKeys;
    }

    /// <summary>A convenience constructor for a LEAF dependency (declares nothing itself).</summary>
    public PackDependencyRef(string key, string version)
        : this(key, version, Array.Empty<string>())
    {
    }
}
