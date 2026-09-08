using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Policy;

/// <summary>
/// Resolves a <see cref="Tag"/> to its <see cref="PolicyBinding"/>. Seeded with the
/// predefined <c>pii</c> / <c>phi</c> / <c>pci</c> / <c>cui</c> bindings (as DATA); a
/// new compliance regime is a new seed binding, not a code change (ADR 0140 D2 §3.1).
/// </summary>
public interface IPolicyRegistry
{
    /// <summary>Returns the binding for <paramref name="tag"/> (keyed by
    /// <c>(System, Code)</c>), or null if the tag binds no policy.</summary>
    PolicyBinding? Resolve(Tag tag);
}
