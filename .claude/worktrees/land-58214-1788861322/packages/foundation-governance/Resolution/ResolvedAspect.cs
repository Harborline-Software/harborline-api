using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Resolution;

/// <summary>
/// The effective aspect at a field after the SPINE-2 form→section→field (and
/// cross-definition lineage) walk, with the per-class resolution semantics applied
/// (ADR 0140 D2 §2): classification monotonic-union, access monotonic-narrowing,
/// lifecycle strengthen-only, residency intersection, presentation/discovery override.
/// </summary>
/// <param name="Field">The field name this aspect resolves for.</param>
/// <param name="Tags">The effective classification tags (union of every grain +
/// the <c>PiiSensitivity.Sensitive</c> sugar).</param>
/// <param name="ReadRoles">Effective read-role set (intersection across grains); null ⇒
/// unconstrained by roles.</param>
/// <param name="WriteRoles">Effective write-role set (intersection); null ⇒ unconstrained.</param>
/// <param name="ReadConditions">Every read condition declared across grains (AND-ed).</param>
/// <param name="Retention">The strongest (longest-floor) retention requirement, or null.</param>
/// <param name="Residency">The intersected residency requirement (allowed = ∩, prohibited = ∪),
/// or null if no grain constrained residency.</param>
/// <param name="Immutability">The strongest immutability declared across grains.</param>
/// <param name="Provenance">The finest-grain provenance override, or null.</param>
/// <param name="Discovery">The finest-grain discovery override, or null.</param>
public sealed record ResolvedAspect(
    string Field,
    IReadOnlyList<Tag> Tags,
    IReadOnlyList<string>? ReadRoles,
    IReadOnlyList<string>? WriteRoles,
    IReadOnlyList<string> ReadConditions,
    RetentionRequirement? Retention,
    ResidencyRequirement? Residency,
    Immutability Immutability,
    Provenance? Provenance,
    DiscoveryAspect? Discovery);
