using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// SPINE-2 (ADR 0140 D2) additive overlay of the eight fractal field aspects,
/// layered onto a grain (form / section / field) of a <see cref="FormDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly additive (back-compat keystone).</b> A definition that carries no
/// <see cref="AspectOverlay"/> at any grain behaves byte-identically to the
/// pre-SPINE-2 substrate — exactly the ADR 0055 posture. The overlay only ever
/// <em>adds</em> governance; it never changes existing behaviour when absent.
/// </para>
/// <para>
/// Five of the eight aspects (identity, data/schema, presentation, access,
/// validation) already live on their existing surfaces
/// (<see cref="HarborlineOverlay.Fields"/> keys, the kernel JSON-Schema body,
/// <see cref="FieldOverlay.Label"/> / <see cref="FieldOverlay.ControlHint"/>,
/// <see cref="SectionAccess"/>, <see cref="RuleDefinition"/>). This overlay carries
/// only the three genuinely net-new aspects — <see cref="Classification"/> (tags →
/// policy), the governance half of lifecycle (<see cref="Lifecycle"/>), and
/// <see cref="Discovery"/> — plus the optional access narrowing the SPINE-2 resolver
/// treats as a monotonic-tighten grain.
/// </para>
/// <para>
/// <b>Pure data, zero governance dependency.</b> Every member is a forms-local
/// primitive / enum / string (regime, jurisdiction, and floor-class are open-vocab
/// strings). The SPINE-2 governance layer (<c>Harborline.Api.Foundation.Governance</c>)
/// interprets these against the regulatory + security-policy enums; the keystone
/// itself stays free of those dependencies.
/// </para>
/// </remarks>
/// <param name="Classification">Tags (open vocabulary) bound to policies. Absent ⇒
/// the field carries only what <see cref="FieldOverlay.PiiSensitivity"/> implies.</param>
/// <param name="Access">Optional access narrowing (read/write roles, read condition)
/// the SPINE-2 resolver enforces as monotonic-tighten over <see cref="SectionAccess"/>.</param>
/// <param name="Lifecycle">Retention · residency · immutability · provenance.</param>
/// <param name="Discovery">Search / reporting facets.</param>
public sealed record AspectOverlay(
    ClassificationAspect? Classification = null,
    AccessAspect? Access = null,
    LifecycleAspect? Lifecycle = null,
    DiscoveryAspect? Discovery = null);

/// <summary>
/// An open-vocabulary classification token — a CodeableConcept (ADR 0056) over the
/// data-classification taxonomy. The richness lives in the <em>policy</em> a tag binds
/// to (in the governance layer), not in the token string.
/// </summary>
/// <param name="System">The coding system (e.g. <c>"shipyard/data-classification"</c>
/// for the predefined <c>pii</c> / <c>phi</c> / <c>pci</c> / <c>cui</c>, or a tenant
/// system such as <c>"acme/labels"</c>). Identity for registry lookup is
/// <c>(System, Code)</c>; <see cref="Display"/> is advisory.</param>
/// <param name="Code">The stable code within <see cref="System"/> (e.g. <c>"pii"</c>).</param>
/// <param name="Display">Optional cached human display string.</param>
public sealed record Tag(string System, string Code, string? Display = null);

/// <summary>
/// The classification aspect (#5) — the tags that drive policy. When present at a
/// finer grain it MUST be a superset of every tag inherited from a coarser grain
/// (monotonic-union); omitting an inherited tag is a relax-attempt the SPINE-2
/// resolver rejects at publish.
/// </summary>
/// <param name="Tags">The classification tags declared at this grain.</param>
public sealed record ClassificationAspect(IReadOnlyList<Tag> Tags);

/// <summary>
/// The access aspect (#6) narrowing — read/write role sets + read condition the
/// SPINE-2 resolver enforces as monotonic-narrowing (intersection) over the
/// section's <see cref="SectionAccess"/>. A finer grain may only TIGHTEN; declaring
/// a role not inherited from a coarser grain is a relax-attempt rejected at publish.
/// </summary>
/// <param name="ReadRoles">Optional read-role narrowing at this grain (null ⇒ inherit).</param>
/// <param name="WriteRoles">Optional write-role narrowing at this grain (null ⇒ inherit).</param>
/// <param name="ReadConditionExpression">Optional additional Tier-1 read condition.</param>
/// <param name="ReadStandings">Optional record-derived read facts.</param>
/// <param name="WriteStandings">Optional record-derived write facts.</param>
public sealed record AccessAspect(
    IReadOnlyList<string>? ReadRoles = null,
    IReadOnlyList<string>? WriteRoles = null,
    string? ReadConditionExpression = null,
    IReadOnlyList<RecordStandingReference>? ReadStandings = null,
    IReadOnlyList<RecordStandingReference>? WriteStandings = null);

/// <summary>
/// The lifecycle aspect (#7) — retention · residency · immutability · provenance.
/// Retention strengthens monotonically (a finer grain may only lengthen), residency
/// intersects (a finer grain may only constrain further), immutability raises
/// monotonically.
/// </summary>
/// <param name="Retention">Optional retention floor declared at this grain.</param>
/// <param name="Residency">Optional residency constraint declared at this grain.</param>
/// <param name="Immutability">Field mutability posture (default <see cref="Models.Immutability.Mutable"/>).</param>
/// <param name="Provenance">Optional provenance (stored / computed / imported).</param>
public sealed record LifecycleAspect(
    RetentionRequirement? Retention = null,
    ResidencyRequirement? Residency = null,
    Immutability Immutability = Immutability.Mutable,
    Provenance? Provenance = null);

/// <summary>
/// A declared retention floor on a field. <see cref="MinimumRetentionDays"/> is the
/// authoring-grain floor the resolver uses for the monotonic-strengthen comparison;
/// the SPINE-2 Store PEP additionally consults the tenant retention resolver and
/// honours whichever floor is longer (floor-wins, ADR 0137 / 0068 §5.2).
/// </summary>
/// <param name="Regime">Open-vocab regulatory regime token (e.g. <c>"HIPAA"</c>,
/// <c>"GDPR"</c>, <c>"PCI_DSS_v4"</c>) — the governance layer maps it to
/// <c>RegulatoryRegime</c> for regime-precedence.</param>
/// <param name="FloorClass">Open-vocab audit-event-class token (e.g. <c>"Identity"</c>,
/// <c>"Financial"</c>) the governance class→AuditEventClass bridge maps to the tenant
/// retention resolver's class axis. An unmappable value is rejected at publish (no
/// silent default window).</param>
/// <param name="MinimumRetentionDays">Authoring-declared minimum hold, in days, used
/// for the deterministic monotonic-strengthen comparison across grains.</param>
public sealed record RetentionRequirement(string Regime, string FloorClass, int MinimumRetentionDays);

/// <summary>
/// A declared data-residency constraint on a field. The SPINE-2 resolver intersects
/// the allowed-jurisdiction sets across grains; an empty effective intersection is an
/// unsatisfiable authoring error rejected at publish.
/// </summary>
/// <param name="AllowedJurisdictions">ISO jurisdiction codes the value MAY reside in.
/// Empty on a classified field is treated fail-closed by the Reside PEP (never the
/// shipped enforcer's fail-open default).</param>
/// <param name="ProhibitedJurisdictions">Optional explicit prohibitions.</param>
public sealed record ResidencyRequirement(
    IReadOnlyList<string> AllowedJurisdictions,
    IReadOnlyList<string>? ProhibitedJurisdictions = null);

/// <summary>Field mutability posture (lifecycle aspect). Ordinal — raises monotonically.</summary>
public enum Immutability
{
    /// <summary>Freely mutable (default).</summary>
    Mutable = 0,

    /// <summary>New values may be appended; prior values are retained.</summary>
    AppendOnly = 1,

    /// <summary>Set once; never changed thereafter.</summary>
    WriteOnce = 2,
}

/// <summary>How a field's value originates (lifecycle aspect, provenance).</summary>
/// <param name="Kind">Stored, computed, or imported.</param>
/// <param name="Source">For <see cref="ProvenanceKind.Computed"/> the rule id; for
/// <see cref="ProvenanceKind.Imported"/> the source system; else null.</param>
public sealed record Provenance(ProvenanceKind Kind, string? Source = null);

/// <summary>The origin kind of a field value.</summary>
public enum ProvenanceKind
{
    /// <summary>Stored directly by a writer.</summary>
    Stored = 0,

    /// <summary>Computed by a rule (SPINE-1); <c>Source</c> carries the rule id.</summary>
    Computed = 1,

    /// <summary>Imported from another system; <c>Source</c> carries the origin.</summary>
    Imported = 2,
}

/// <summary>
/// The discovery and reporting aspect (#8) — search / identifier / facet / measure /
/// reportable flags. Pure metadata consumed by the search + reporting surfaces.
/// </summary>
/// <param name="Searchable">Field participates in full-text / index search.</param>
/// <param name="Identifier">Field is a business identifier (deduplication / lookup).</param>
/// <param name="Facet">Field is a filterable facet.</param>
/// <param name="Measure">Reporting role (measure / dimension / none).</param>
/// <param name="Reportable">Field may appear in reports / exports.</param>
public sealed record DiscoveryAspect(
    bool Searchable = false,
    bool Identifier = false,
    bool Facet = false,
    MeasureRole Measure = MeasureRole.None,
    bool Reportable = false);

/// <summary>Reporting role of a field (discovery aspect).</summary>
public enum MeasureRole
{
    /// <summary>Neither a measure nor a dimension.</summary>
    None = 0,

    /// <summary>A numeric measure (aggregated in reports).</summary>
    Measure = 1,

    /// <summary>A dimension (grouped / pivoted in reports).</summary>
    Dimension = 2,
}
