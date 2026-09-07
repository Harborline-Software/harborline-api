namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// The regulatory bucket of the domain a pack automates (ADR 0145 D1). Drives which downstream
/// compliance gates apply and — via the counsel-cleared enum set (ADR 0145 D4) — whether the pack may
/// be exported at all: a <see cref="RegulatoryClass"/> value with no cleared counsel row HARD-BLOCKS at
/// the export gate (fail-closed, S-13; no install-anyway path).
/// </summary>
/// <remarks>
/// <para>
/// <b>Counsel-gated (ADR 0145 D4).</b> Adding a value here is an ADR-amendment-class change AND requires
/// a CLEARED counsel row before the value ships (register rows D-11/D-12). The set of values the export
/// gate accepts is NOT this enum's full membership — it is the intersection with the counsel-cleared
/// config (<c>Validation/dcp-counsel-cleared-classes.json</c>, read by <see cref="IDcpCounselRegister"/>).
/// </para>
/// <para>
/// <b>Dogfood posture (ADR 0145 open-Q1).</b> <see cref="General"/> is cleared; <see cref="Financial"/>
/// and <see cref="Employment"/> are the open-Q1 lean (pending register rows D-11/D-12);
/// <see cref="Clinical"/> and <see cref="SafetyCritical"/> carry the heaviest statutory load and block on
/// counsel. The committed cleared set is the single source — see the JSON config, not this comment.
/// </para>
/// </remarks>
public enum RegulatoryClass
{
    /// <summary>General/low-risk business automation — the grandfather default for pre-DCP packs
    /// (ADR 0145 compatibility plan). The only class cleared by default.</summary>
    General = 0,

    /// <summary>Financial-domain automation (ledgers, AR/AP). Open-Q1 lean; blocks pending a counsel row.</summary>
    Financial = 1,

    /// <summary>Clinical / health automation (HIPAA-class). Heaviest statutory load; blocks pending counsel.</summary>
    Clinical = 2,

    /// <summary>Employment / HR automation. Open-Q1 lean; blocks pending a counsel row.</summary>
    Employment = 3,

    /// <summary>Safety-critical / life-safety automation. Blocks pending counsel.</summary>
    SafetyCritical = 4,
}

/// <summary>
/// The data-sensitivity classification the pack's domain implicates (ADR 0139 seam). The pack DECLARES
/// its intent here; the installing instance RE-DERIVES the authoritative class against its own ADR 0139
/// registry (ADR 0145 D3.2 / 0143 F2 / S-9) — the pack label is non-authoritative. v1 defines the
/// conservative ladder; the 0139 registry vocabulary firms it up when 0139 builds (re-derivation seam,
/// stubbed install-side in Phase 1).
/// </summary>
public enum DataSensitivityClass
{
    /// <summary>Non-sensitive / publishable domain data.</summary>
    Public = 0,

    /// <summary>Internal-only domain data (the conservative default).</summary>
    Internal = 1,

    /// <summary>Confidential domain data (contractual / commercial sensitivity).</summary>
    Confidential = 2,

    /// <summary>Restricted domain data (regulated / special-category personal data).</summary>
    Restricted = 3,
}

/// <summary>
/// What the FINAL stage of an accountable-individual grace ladder does when the vacancy persists
/// (ADR 0145 BF-1 / ADR 0144 D6). There is DELIBERATELY no <c>Lock</c>/<c>Suspend</c> member: the
/// platform NEVER hard-locks the tenant out of their own instance (ADR 0144 D6, CIC RULED Q4 —
/// escalate-and-warn only). The absence is structural, not a policy default, so a future edit cannot
/// silently add a hard-lock endpoint without an enum change that review will catch.
/// </summary>
public enum EndpointDisposition
{
    /// <summary>Keep escalating warnings — the softest endpoint (the ladder simply does not stop warning).</summary>
    EscalateAndWarn = 0,

    /// <summary>Notify the accountable chain (the regime's designated escalation contacts).</summary>
    NotifyAccountableChain = 1,

    /// <summary>Restrict NON-ESSENTIAL actions only — never the essential/read path, never a full lock.</summary>
    RestrictNonEssential = 2,
}
