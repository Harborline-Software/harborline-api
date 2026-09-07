using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Policy;

/// <summary>
/// The predefined data-classification policy bindings (<c>pii</c> / <c>phi</c> /
/// <c>pci</c> / <c>cui</c>), shipped as DATA (ADR 0140 D2 §3.1). A new compliance
/// regime is a new seed binding, not a code change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Legal note (ADR 0068 §GC.1).</b> Retention and residency thresholds below are
/// informed by HIPAA / PCI-DSS / GDPR presets and are NOT legal advice — deployers MUST
/// obtain qualified legal counsel before relying on them for compliance attestation.
/// PCI-DSS is <c>ExplicitlyDisclaimedOpenSource</c> (the <c>pci</c> binding is provided
/// for completeness; the canonical in-scope retention illustration is HIPAA).
/// </para>
/// <para>
/// A <see cref="EffectKind.Reside"/> effect here carries NO allowed-jurisdiction set —
/// the set comes from the field's <c>ResidencyRequirement</c> (authoring), intersected
/// across grains. A classified field with a Reside effect but no residency requirement
/// is rejected fail-closed by the Store/Export PEP (the eligible-set bridge).
/// </para>
/// <para>
/// <b>At-rest key granularity FOLLOWS the classification (ADR 0139 amendment 2026-06-30, HYBRID).</b>
/// Plain <c>pii</c> (<see cref="PiiSensitivity.Sensitive"/>) encrypts under the <b>tenant DEK</b>
/// (<c>SubjectScoped: false</c>) — byte-identical to the legacy
/// <c>FormEngine.EncryptSensitiveFieldsAsync</c> envelope: no subject required, no migration.
/// The <b><c>identifier</c></b> class (the D3 re-identification element) and <c>phi</c> (the D5
/// HIPAA identifiers / health record) encrypt under the <b>per-subject DEK</b>
/// (<c>SubjectScoped: true</c> ⇒ <c>HKDF-Expand(tenantDEK, "subject-dek-v1:"||subjectId)</c>) so a
/// single subject can be crypto-shredded (GDPR Art-17, ADR 0135). <c>pci</c> / <c>cui</c> are
/// non-identity-keyed ⇒ tenant DEK. The per-subject path requires the <c>subjectRef</c>
/// primary-subject seam (ADR 0139 D3) before any per-subject field is wired; plain <c>pii</c>
/// needs no subject.
/// </para>
/// </remarks>
public static class PredefinedPolicyBindings
{
    /// <summary>
    /// The coding system for the predefined data-classification tags (ADR 0056) — the
    /// <b>canonical WRITE value for this release window</b>. Ticket 260 renames it to
    /// <see cref="DataClassificationSystemRenamed"/>; readers accept BOTH from this release
    /// (<see cref="AcceptedDataClassificationSystems"/>), and the write value flips only once every
    /// consumer accepts both (the platform projection still carries its own copy of this literal in
    /// <c>hlp.foundation.forms-engine/Security/TenantBoundAesGcmFormFieldSecurity.cs</c>, and the
    /// package-consumer fixture emits it).
    /// </summary>
    public const string DataClassificationSystem = "shipyard/data-classification";

    /// <summary>
    /// The renamed data-classification system id (ticket 260). Recognised as canonical on READ from
    /// this release — it binds to the same predefined policies and is held to the same fail-closed
    /// admission checks as <see cref="DataClassificationSystem"/> — but is NOT yet emitted on the
    /// wire or written to storage.
    /// </summary>
    public const string DataClassificationSystemRenamed = "harborline/data-classification";

    /// <summary>
    /// Every system id that IS the predefined data-classification system. A tag in ANY member binds
    /// to the same predefined policy and is subject to the same unknown-kind and near-miss rejections;
    /// a near-miss of ANY member is rejected. The retired member leaves the set in a later slice.
    /// </summary>
    public static IReadOnlyList<string> AcceptedDataClassificationSystems { get; } = new[]
    {
        DataClassificationSystem, DataClassificationSystemRenamed,
    };

    /// <summary>
    /// True when <paramref name="system"/> is one of <see cref="AcceptedDataClassificationSystems"/>
    /// (ordinal). Callers that tolerate whitespace trim BEFORE calling; this compares raw so a
    /// whitespace-dirty stored tag cannot resolve to a binding it does not exactly name.
    /// </summary>
    public static bool IsDataClassificationSystem(string? system)
        => system is not null
            && AcceptedDataClassificationSystems.Contains(system, StringComparer.Ordinal);

    /// <summary>HIPAA medical-record retention floor (≈6 years), in days.</summary>
    public const int HipaaRetentionDays = 2190;

    /// <summary>PCI-DSS audit-trail retention floor (≈1 year), in days.</summary>
    public const int PciRetentionDays = 365;

    /// <summary>The predefined <c>pii</c> tag (the back-compat alias for <c>PiiSensitivity.Sensitive</c>).</summary>
    public static Tag Pii { get; } = new(DataClassificationSystem, "pii", "Personally identifiable information");

    /// <summary>
    /// The predefined <c>identifier</c> tag — the ADR 0139 D3 re-identification element (the
    /// identifying subset of PII: direct identifiers / quasi-identifiers). Sealed under the
    /// per-subject DEK so it is independently crypto-shreddable (the hybrid key model).
    /// </summary>
    public static Tag Identifier { get; } =
        new(DataClassificationSystem, "identifier", "Re-identification element (identifying PII)");

    /// <summary>The predefined <c>phi</c> tag (protected health information).</summary>
    public static Tag Phi { get; } = new(DataClassificationSystem, "phi", "Protected health information");

    /// <summary>The predefined <c>pci</c> tag (payment-card data).</summary>
    public static Tag Pci { get; } = new(DataClassificationSystem, "pci", "Payment-card data");

    /// <summary>The predefined <c>cui</c> tag (controlled unclassified information).</summary>
    public static Tag Cui { get; } = new(DataClassificationSystem, "cui", "Controlled unclassified information");

    private static readonly IReadOnlyList<Trigger> Store = new[] { Trigger.Store };
    private static readonly IReadOnlyList<Trigger> ReadExport = new[] { Trigger.Read, Trigger.Export };
    private static readonly IReadOnlyList<Trigger> StoreReadExport =
        new[] { Trigger.Store, Trigger.Read, Trigger.Export };
    private static readonly IReadOnlyList<Trigger> ReadOnlyAudit = new[] { Trigger.Read };

    /// <summary>
    /// The <c>pii</c> binding — byte-identical to today's <c>PiiSensitivity.Sensitive</c>
    /// behaviour: <c>Encrypt@Store</c> (under the <b>tenant DEK</b>, <c>SubjectScoped: false</c>)
    /// + <c>Redact@Read</c> + <c>Audit@Read</c> (the back-compat keystone, ADR 0140 D2 §3.2;
    /// the ADR 0139 hybrid key model). The class requires Encrypt@Store + Audit. The identifying
    /// subset that warrants single-subject crypto-shred is the separate <c>identifier</c> class.
    /// </summary>
    public static PolicyBinding PiiBinding { get; } = new(
        Pii,
        new TagClass("pii", new[]
        {
            new RequiredEffect(EffectKind.Encrypt, Trigger.Store),
            new RequiredEffect(EffectKind.Audit),
        }),
        new[]
        {
            // Tenant DEK — byte-identical to legacy FormEngine.EncryptSensitiveFieldsAsync (no subject).
            new PolicyEffect(EffectKind.Encrypt, Store, new EffectParams(SubjectScoped: false)),
            new PolicyEffect(EffectKind.Redact, ReadExport),
            new PolicyEffect(EffectKind.Audit, ReadOnlyAudit),
        });

    /// <summary>
    /// The <c>identifier</c> binding — the ADR 0139 D3 re-identification element. Same effect
    /// shape as <c>pii</c> (<c>Encrypt@Store</c> + <c>Redact@Read/Export</c> + <c>Audit@Read</c>)
    /// but sealed under the <b>per-subject DEK</b> (<c>SubjectScoped: true</c>) so a single
    /// subject's identifiers can be crypto-shredded (GDPR Art-17 / ADR 0135). Requires the
    /// <c>subjectRef</c> primary-subject seam (ADR 0139 D3) before it is wired.
    /// </summary>
    public static PolicyBinding IdentifierBinding { get; } = new(
        Identifier,
        new TagClass("identifier", new[]
        {
            new RequiredEffect(EffectKind.Encrypt, Trigger.Store),
            new RequiredEffect(EffectKind.Audit),
        }),
        new[]
        {
            // Per-subject DEK — independently crypto-shreddable (single-subject erasure).
            new PolicyEffect(EffectKind.Encrypt, Store, new EffectParams(SubjectScoped: true)),
            new PolicyEffect(EffectKind.Redact, ReadExport),
            new PolicyEffect(EffectKind.Audit, ReadOnlyAudit),
        });

    /// <summary>The <c>phi</c> binding — encrypt (per-subject) + redact + audit + reside + retain (HIPAA).</summary>
    public static PolicyBinding PhiBinding { get; } = new(
        Phi,
        new TagClass("phi", new[]
        {
            new RequiredEffect(EffectKind.Encrypt, Trigger.Store),
            new RequiredEffect(EffectKind.Audit),
            new RequiredEffect(EffectKind.Reside),
            new RequiredEffect(EffectKind.Retain),
        }),
        new[]
        {
            new PolicyEffect(EffectKind.Encrypt, Store, new EffectParams(SubjectScoped: true)),
            new PolicyEffect(EffectKind.Redact, ReadExport),
            new PolicyEffect(EffectKind.Audit, StoreReadExport),
            new PolicyEffect(EffectKind.Reside, StoreReadExport),
            new PolicyEffect(EffectKind.Retain, Store,
                new EffectParams(RetainRegime: "HIPAA", RetainFloorClass: "Identity",
                    RetainMinimumDays: HipaaRetentionDays)),
        });

    /// <summary>The <c>pci</c> binding — encrypt + audit + retain (PCI) + reside.</summary>
    public static PolicyBinding PciBinding { get; } = new(
        Pci,
        new TagClass("pci", new[]
        {
            new RequiredEffect(EffectKind.Encrypt, Trigger.Store),
            new RequiredEffect(EffectKind.Audit),
            new RequiredEffect(EffectKind.Retain),
        }),
        new[]
        {
            new PolicyEffect(EffectKind.Encrypt, Store, new EffectParams(SubjectScoped: false)),
            new PolicyEffect(EffectKind.Audit, StoreReadExport),
            new PolicyEffect(EffectKind.Retain, Store,
                new EffectParams(RetainRegime: "PCI_DSS_v4", RetainFloorClass: "Financial",
                    RetainMinimumDays: PciRetentionDays)),
            new PolicyEffect(EffectKind.Reside, Store),
        });

    /// <summary>The <c>cui</c> binding — encrypt + redact + audit + reside.</summary>
    public static PolicyBinding CuiBinding { get; } = new(
        Cui,
        new TagClass("cui", new[]
        {
            new RequiredEffect(EffectKind.Encrypt, Trigger.Store),
            new RequiredEffect(EffectKind.Audit),
            new RequiredEffect(EffectKind.Reside),
        }),
        new[]
        {
            new PolicyEffect(EffectKind.Encrypt, Store, new EffectParams(SubjectScoped: false)),
            new PolicyEffect(EffectKind.Redact, ReadExport),
            new PolicyEffect(EffectKind.Audit, StoreReadExport),
            new PolicyEffect(EffectKind.Reside, StoreReadExport),
        });

    /// <summary>All predefined bindings, in a stable order.</summary>
    public static IReadOnlyList<PolicyBinding> All { get; } = new[]
    {
        PiiBinding, IdentifierBinding, PhiBinding, PciBinding, CuiBinding,
    };
}
