using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Policy;

/// <summary>
/// A declarative binding of a <see cref="Tag"/> to the effects it governs. The whole
/// point of SPINE-2: a binding is a static <c>effects × triggers</c> map over BUILT
/// primitives — predefined bindings ship as DATA, not code (ADR 0140 D2 §3.1).
/// </summary>
/// <param name="Tag">The classification token this policy governs.</param>
/// <param name="Class">Optional security/compliance class — when set, the fail-closed
/// admission validator requires the binding's effects to cover the class's required
/// effects (refuse-on-absence).</param>
/// <param name="Effects">The effects this tag applies.</param>
public sealed record PolicyBinding(
    Tag Tag,
    TagClass? Class,
    IReadOnlyList<PolicyEffect> Effects);

/// <summary>
/// A security/compliance class declaring the effects every field carrying a tag of this
/// class MUST have. Drives the fail-closed publish-time validation (ADR 0140 D2 §3.4,
/// modeled on the ADR 0135 A1 validator).
/// </summary>
/// <param name="Name">Class name (e.g. <c>"pii"</c>, <c>"phi"</c>).</param>
/// <param name="RequiredEffects">The effects a compliant binding must cover.</param>
public sealed record TagClass(string Name, IReadOnlyList<RequiredEffect> RequiredEffects);

/// <summary>
/// A required (effect, trigger) a <see cref="TagClass"/> mandates. A null
/// <see cref="Trigger"/> means "this effect at any trigger".
/// </summary>
/// <param name="Kind">The required effect kind.</param>
/// <param name="Trigger">The required trigger, or null for "any trigger".</param>
public sealed record RequiredEffect(EffectKind Kind, Trigger? Trigger = null);
