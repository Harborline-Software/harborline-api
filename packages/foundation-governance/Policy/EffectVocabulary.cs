namespace Harborline.Api.Foundation.Governance.Policy;

/// <summary>
/// What a policy effect DOES at a trigger. Each kind is a thin adapter over a BUILT
/// primitive (encrypt / shred / retain / reside / audit / render-redaction); SPINE-2
/// composes them, it does not re-implement them (ADR 0140 D2).
/// </summary>
/// <remarks>
/// The set aligns to the ADR 0064 regulatory-policy vocabulary
/// (<c>PolicyEnforcementAction</c>) at field grain rather than inventing a parallel
/// taxonomy. <see cref="Encrypt"/> · <see cref="Audit"/> · <see cref="Redact"/> ·
/// <see cref="Mask"/> · <see cref="Consent"/> compose freely (union). <see cref="Reside"/>
/// intersects. <see cref="Retain"/> vs <see cref="Erase"/> are reconciled by the
/// legal-hold &gt; retention-floor &gt; erase precedence lattice (crypto-shred, not deletion).
/// </remarks>
public enum EffectKind
{
    /// <summary>Encrypt the field at rest (tenant DEK, or per-subject DEK when the
    /// effect is subject-scoped) — composes <c>IFieldEncryptor</c> / <c>ISubjectFieldEncryptor</c>.</summary>
    Encrypt = 0,

    /// <summary>Omit the value entirely for an unauthorized reader — composes the
    /// form-engine render redaction (<c>IsReadable=false</c>, value nulled).</summary>
    Redact = 1,

    /// <summary>Partially reveal the value (e.g. <c>****1234</c>) — the net-new render path.</summary>
    Mask = 2,

    /// <summary>Append a signed, hash-chained audit record — composes <c>IAuditLog</c>.</summary>
    Audit = 3,

    /// <summary>Set / observe the retention clock — composes <c>IRetentionPolicyResolver</c> (floor-wins).</summary>
    Retain = 4,

    /// <summary>Crypto-shred the per-subject key — composes <c>ISubjectErasureService</c>.</summary>
    Erase = 5,

    /// <summary>Constrain the data-residency region — composes <c>IDataResidencyEnforcer</c>
    /// (upgraded to fail-closed eligible-set for classified fields).</summary>
    Reside = 6,

    /// <summary>Require a consent record before read / export — composes <c>IConsentGate</c> (fail-closed).</summary>
    Consent = 7,
}

/// <summary>
/// The lifecycle point at which an effect fires — the four PEPs (ADR 0140 D2 §4).
/// </summary>
public enum Trigger
{
    /// <summary>The write path (<c>IFormEngine.SaveAsync</c>, inside the ADR 0126 atomic txn).</summary>
    Store = 0,

    /// <summary>The render path (<c>IFormEngine.RenderAsync</c> → <c>FormView</c>).</summary>
    Read = 1,

    /// <summary>The export / report projection path.</summary>
    Export = 2,

    /// <summary>The crypto-shred path (<c>ISubjectErasureService.EraseAsync</c>).</summary>
    EraseSubject = 3,
}
