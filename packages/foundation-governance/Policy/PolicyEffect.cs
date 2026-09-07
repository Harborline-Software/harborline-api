namespace Harborline.Api.Foundation.Governance.Policy;

/// <summary>
/// One effect of a policy — an <see cref="EffectKind"/> bound to the
/// <see cref="Trigger"/>s at which it fires, plus its typed <see cref="EffectParams"/>.
/// A policy is a static, declarative <c>effects × triggers</c> map — NOT a typed
/// enum of policy-kinds and NOT a workflow (ADR 0140 D2 §3.5).
/// </summary>
/// <param name="Kind">What the effect does.</param>
/// <param name="Triggers">The PEPs at which it fires.</param>
/// <param name="Params">Typed parameters for the effect (mask reveal-count, retention
/// regime/floor, residency allowed-set, subject-scoping, consent purpose).</param>
public sealed record PolicyEffect(
    EffectKind Kind,
    IReadOnlyList<Trigger> Triggers,
    EffectParams Params)
{
    /// <summary>Convenience: an effect with empty params.</summary>
    public PolicyEffect(EffectKind kind, IReadOnlyList<Trigger> triggers)
        : this(kind, triggers, EffectParams.None) { }
}

/// <summary>
/// Typed parameters for a <see cref="PolicyEffect"/>. All optional — only the members
/// relevant to the <see cref="PolicyEffect.Kind"/> are read.
/// </summary>
/// <param name="MaskRevealLast">For <see cref="EffectKind.Mask"/> — reveal the last N
/// characters, mask the rest (e.g. <c>4</c> ⇒ <c>****1234</c>). Null ⇒ reveal none.</param>
/// <param name="SubjectScoped">For <see cref="EffectKind.Encrypt"/> — true ⇒ seal under
/// the per-subject DEK (independently crypto-shreddable) rather than the tenant DEK.</param>
/// <param name="RetainRegime">For <see cref="EffectKind.Retain"/> — open-vocab regime
/// token (e.g. <c>"HIPAA"</c>) mapped to <c>RegulatoryRegime</c> for precedence.</param>
/// <param name="RetainFloorClass">For <see cref="EffectKind.Retain"/> — audit-event-class
/// token (e.g. <c>"Identity"</c>) the class→AuditEventClass bridge maps to the tenant
/// retention resolver. Unmappable ⇒ reject publish (no silent default window).</param>
/// <param name="RetainMinimumDays">For <see cref="EffectKind.Retain"/> — authoring floor in days.</param>
/// <param name="ResideAllowedJurisdictions">For <see cref="EffectKind.Reside"/> — the
/// allowed-jurisdiction set. Empty on a classified field ⇒ fail-closed reject (never the
/// shipped enforcer's fail-open default).</param>
/// <param name="ConsentPurpose">For <see cref="EffectKind.Consent"/> — the purpose token
/// the consent record must cover.</param>
public sealed record EffectParams(
    int? MaskRevealLast = null,
    bool SubjectScoped = false,
    string? RetainRegime = null,
    string? RetainFloorClass = null,
    int? RetainMinimumDays = null,
    IReadOnlyList<string>? ResideAllowedJurisdictions = null,
    string? ConsentPurpose = null)
{
    /// <summary>The empty params instance.</summary>
    public static EffectParams None { get; } = new();
}
