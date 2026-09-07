namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// The Domain Compliance Profile (ADR 0145 D1) — the vendor-side governance artifact every domain pack
/// MUST carry. It records WHO is accountable for the pack's domain, WHAT regulatory class it carries,
/// WHAT it is explicitly out-of-scope for, and what audience / operator / jurisdictional / data-sensitivity
/// constraints apply. The DCP <b>records</b> compliance intent; it never <b>asserts</b> the pack SATISFIES
/// any statute (the counsel-grade honesty boundary, D-11).
/// </summary>
/// <remarks>
/// <para>
/// <b>A DISTINCT, SIGNED SUB-RECORD (council Q1b fold).</b> The DCP is authored inline on the composition
/// surface but is packaged as its OWN content-address leaf (see <c>PackDcpRef</c> on the manifest +
/// <c>PackDcpPayload</c> on the pack file), NOT as loose manifest fields. Because it is separately
/// attributable, the solo→multi RACI fan-out (D2 — Designer authors, Arbiter clears, no self-Arbiter) is a
/// later PERMISSION change, not a re-architecture: the DCP already has its own signed leaf to attach
/// authorship to.
/// </para>
/// <para>
/// <b>Signed + tamper-evident (D3.4).</b> The DCP's content-address is bound into the SIGNED manifest, so
/// signing the pack merkle-binds the DCP; a swapped DCP payload fails the verify-time content-address
/// re-hash exactly like any inner content item.
/// </para>
/// <para>
/// <b>Classification is non-authoritative (D3.2 / S-9).</b> The classification-bearing claims
/// (<see cref="RegulatoryClass"/>, <see cref="DataSensitivity"/>) are RE-DERIVED at install against the
/// platform's own ADR 0128/0139 registries; a mismatch refuses install. The export gate validates the
/// DECLARED DCP (presence + schema + counsel-cleared enum); the installing instance is the authority. The
/// install-side re-derivation is a Phase-1 STUB.
/// </para>
/// </remarks>
/// <param name="RegulatoryClass">The domain's regulatory bucket (D1). A non-counsel-cleared value
/// HARD-BLOCKS export (D4 / S-13).</param>
/// <param name="AccountableIndividualRegimes">The statutory "responsible person" regimes the domain
/// implicates (0144 D6). RECORDS the binding; does not imply the pack satisfies the statute (D-11). The
/// per-regime grace-ladder endpoints live here (ADR 0145 BF-1).</param>
/// <param name="Audience">Audience + age-access posture. v1 DECLINES minors-as-principals absent a counsel
/// pass (D-10).</param>
/// <param name="OperatorCapacity">Operator age / capacity constraints for who may operate the pack's
/// consequential actions.</param>
/// <param name="OutOfScope">Explicit out-of-scope / presumptive-decline declarations. Wording is
/// counsel-grade, referenced by FK into the counsel-owned disclaimer taxonomy (D-12) — never free text.</param>
/// <param name="JurisdictionalVariance">Jurisdictional variance / life-safety / data-sensitivity variance
/// (rides ADR 0064 cascade). Counsel-gated when declared (D-13).</param>
/// <param name="DataSensitivity">The declared ADR 0139 data-sensitivity class (re-derived install-side).</param>
/// <param name="Raci">The vendor RACI (Designer / Arbiter / Steward / Curator). Solo-collapsed to the
/// authoring principal today (D2).</param>
/// <param name="DcpVersion">The DCP's own version string. Pack-locked today (open-Q3 lean — a DCP change is
/// a pack change).</param>
public sealed record DomainComplianceProfile(
    RegulatoryClass RegulatoryClass,
    IReadOnlyList<AccountableIndividualRegime> AccountableIndividualRegimes,
    AudiencePosture Audience,
    OperatorCapacity OperatorCapacity,
    IReadOnlyList<DcpOutOfScopeDeclaration> OutOfScope,
    JurisdictionalVariance JurisdictionalVariance,
    DataSensitivityClass DataSensitivity,
    DcpRaci Raci,
    string DcpVersion)
{
    /// <summary>
    /// The grandfather DCP for the general/low-risk case (ADR 0145 compatibility plan): <see cref="RegulatoryClass.General"/>,
    /// empty out-of-scope, no regimes, no variance, <see cref="DataSensitivityClass.Internal"/>, RACI
    /// solo-collapsed to <paramref name="authoringPrincipal"/>. This is what a pre-DCP / dogfood pack
    /// (e.g. the #127 General pack) declares when the author supplies no explicit profile.
    /// </summary>
    public static DomainComplianceProfile General(string authoringPrincipal, string dcpVersion = "1.0.0")
        => new(
            RegulatoryClass: RegulatoryClass.General,
            AccountableIndividualRegimes: Array.Empty<AccountableIndividualRegime>(),
            Audience: AudiencePosture.GeneralAudience,
            OperatorCapacity: OperatorCapacity.Unconstrained,
            OutOfScope: Array.Empty<DcpOutOfScopeDeclaration>(),
            JurisdictionalVariance: JurisdictionalVariance.None,
            DataSensitivity: DataSensitivityClass.Internal,
            Raci: DcpRaci.SoloCollapsed(authoringPrincipal),
            DcpVersion: dcpVersion);
}

/// <summary>
/// One accountable-individual statutory regime the pack's domain implicates (ADR 0145 BF-1, superseding
/// the flat <c>IReadOnlyList&lt;string&gt;</c>). The DCP RECORDS the regime binding + its grace-ladder
/// endpoint; it does NOT imply the pack satisfies the statute (counsel D-11).
/// </summary>
/// <param name="RegimeId">The regime identifier, e.g. <c>"BSA-AML-officer"</c>, <c>"HIPAA-privacy-officer"</c>.</param>
/// <param name="StatutoryBasisRef">FK into the counsel-reviewed statutory-basis taxonomy (D-11) — NOT free text.</param>
/// <param name="GracePolicy">The escalate-and-warn ladder endpoint (ADR 0144 D6) DCP-configurable per regime.</param>
public sealed record AccountableIndividualRegime(
    string RegimeId,
    string StatutoryBasisRef,
    GracePolicy GracePolicy);

/// <summary>
/// The per-regime grace policy (ADR 0145 BF-1 / ADR 0144 D6) — an ordered escalate-and-warn ladder plus a
/// terminal <see cref="EndpointDisposition"/> that is STRUCTURALLY never a hard-lock (the enum has no
/// Lock/Suspend member).
/// </summary>
/// <param name="Stages">The ordered escalate-and-warn stages (may be empty for a regime that only records).</param>
/// <param name="Endpoint">What the final stage does — never a hard-lock (0144 D6, CIC RULED Q4).</param>
public sealed record GracePolicy(
    IReadOnlyList<GraceLadderStage> Stages,
    EndpointDisposition Endpoint)
{
    /// <summary>A record-only grace policy: no stages, softest endpoint. The default for a regime the DCP
    /// merely BINDS without configuring a ladder.</summary>
    public static GracePolicy RecordOnly { get; } =
        new(Array.Empty<GraceLadderStage>(), EndpointDisposition.EscalateAndWarn);
}

/// <summary>
/// One stage of an accountable-individual grace ladder (ADR 0145 BF-1). Escalate-and-warn only — a stage
/// RESTRICTS non-essential actions and warns; it never locks.
/// </summary>
/// <param name="Order">The stage's position in the ladder (ascending).</param>
/// <param name="Duration">How long this stage lasts before the ladder advances.</param>
/// <param name="Restrictions">What this stage restricts (escalate-and-warn scope only).</param>
/// <param name="WarningRef">FK into the counsel-reviewed notice taxonomy (D-11) — NOT free text.</param>
public sealed record GraceLadderStage(
    int Order,
    TimeSpan Duration,
    IReadOnlyList<string> Restrictions,
    string WarningRef);

/// <summary>
/// Audience + age-access posture (ADR 0145 D1). v1 DECLINES minors-as-PRINCIPALS (counsel D-10) — the
/// export gate refuses a DCP that sets <see cref="MinorsAsPrincipals"/> because no counsel path exists yet.
/// Minors as data SUBJECTS is permitted (a pediatric-intake pack has minors as subjects).
/// </summary>
/// <param name="Audience">A free descriptor of the intended audience (e.g. <c>"general"</c>, <c>"clinicians"</c>).</param>
/// <param name="MinimumOperatorAge">The minimum operator age, if the domain implies one; <c>null</c> = none.</param>
/// <param name="MinorsAsSubjects">True if the domain has minors as DATA SUBJECTS (permitted).</param>
/// <param name="MinorsAsPrincipals">True if the domain has minors as PRINCIPALS/operators (BLOCKED v1 — D-10).</param>
public sealed record AudiencePosture(
    string Audience,
    int? MinimumOperatorAge,
    bool MinorsAsSubjects,
    bool MinorsAsPrincipals)
{
    /// <summary>The general-audience default: no age constraint, no minors as principals.</summary>
    public static AudiencePosture GeneralAudience { get; } =
        new("general", MinimumOperatorAge: null, MinorsAsSubjects: false, MinorsAsPrincipals: false);
}

/// <summary>
/// Operator age / capacity constraints for who may operate the pack's consequential actions (ADR 0145 D1).
/// </summary>
/// <param name="MinimumOperatorAge">Minimum age to operate consequential actions; <c>null</c> = none.</param>
/// <param name="RequiredCapacities">Capacity/credential tokens an operator must hold (opaque refs).</param>
public sealed record OperatorCapacity(
    int? MinimumOperatorAge,
    IReadOnlyList<string> RequiredCapacities)
{
    /// <summary>No operator constraints (the general default).</summary>
    public static OperatorCapacity Unconstrained { get; } =
        new(MinimumOperatorAge: null, RequiredCapacities: Array.Empty<string>());
}

/// <summary>
/// An explicit out-of-scope / presumptive-decline declaration (ADR 0145 D1). Liability-limiting; the
/// disclaimer WORDING is counsel-grade and referenced by FK into a counsel-owned taxonomy (D-12) — the
/// pack author references an entry, never authors the wording.
/// </summary>
/// <param name="Scope">The out-of-scope area, e.g. <c>"regulated financial advice"</c>.</param>
/// <param name="CounselDisclaimerRef">FK into the counsel-reviewed disclaimer taxonomy (D-12) — NOT free text.</param>
public sealed record DcpOutOfScopeDeclaration(
    string Scope,
    string CounselDisclaimerRef);

/// <summary>
/// Jurisdictional variance / life-safety / data-sensitivity variance the pack declares (ADR 0145 D1,
/// rides ADR 0064). When variance is declared it edges toward regulated legal content and is counsel-gated
/// (D-13) — so a declared variance MUST carry a counsel disclaimer FK.
/// </summary>
/// <param name="HasVariance">True if the pack declares jurisdiction-varying governance/fiduciary defaults.</param>
/// <param name="Jurisdictions">The jurisdictions the variance spans (opaque region tags).</param>
/// <param name="CounselDisclaimerRef">FK into the counsel-reviewed variance taxonomy (D-13) when
/// <see cref="HasVariance"/>; <c>null</c> when no variance is declared.</param>
public sealed record JurisdictionalVariance(
    bool HasVariance,
    IReadOnlyList<string> Jurisdictions,
    string? CounselDisclaimerRef)
{
    /// <summary>No declared jurisdictional variance (the general default).</summary>
    public static JurisdictionalVariance None { get; } =
        new(HasVariance: false, Jurisdictions: Array.Empty<string>(), CounselDisclaimerRef: null);
}

/// <summary>
/// The vendor RACI naming four vendor-internal roles (ADR 0145 D2): Designer (authors the domain model +
/// DCP claims), Arbiter (adjudicates DCP claims — the decision role), Steward (owns lifecycle + compliance
/// posture over time), Curator (catalog admission + ongoing curation). Today all four SOLO-COLLAPSE to the
/// authoring principal (CIC); the fleet's review lanes are the compensating control until the roles fan out
/// to distinct humans, at which point Designer can no longer self-Arbiter its own regulatory-class claim
/// (the SoD forbidden-pair of 0144 D1).
/// </summary>
/// <param name="Designer">Authors the pack's domain model + its DCP claims.</param>
/// <param name="Arbiter">Adjudicates DCP claims (regulatory class, out-of-scope).</param>
/// <param name="Steward">Owns the pack's lifecycle + compliance posture over time.</param>
/// <param name="Curator">Catalog/marketplace admission + ongoing curation.</param>
public sealed record DcpRaci(
    string Designer,
    string Arbiter,
    string Steward,
    string Curator)
{
    /// <summary>The solo-collapsed RACI: all four roles held by <paramref name="principal"/> (CIC today,
    /// D2). The solo→multi expansion replaces individual fields as distinct humans take each role.</summary>
    public static DcpRaci SoloCollapsed(string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        return new DcpRaci(principal, principal, principal, principal);
    }

    /// <summary>True iff every role is held by the same principal — the solo-collapse invariant today, and
    /// the shape the export gate expects until the RACI fans out (the self-Arbiter fence is a later
    /// multi-principal follow-up, not enforced here).</summary>
    public bool IsSoloCollapsed =>
        string.Equals(Designer, Arbiter, StringComparison.Ordinal)
        && string.Equals(Arbiter, Steward, StringComparison.Ordinal)
        && string.Equals(Steward, Curator, StringComparison.Ordinal);
}
