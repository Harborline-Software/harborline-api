using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// The kind of a <see cref="ReusableUnit"/> — the discriminator that makes the reuse
/// primitive a <b>single reuse-by-reference model for both forms and workflows</b>
/// (D4, ADR 0135 amendment 2026-07-01).
/// </summary>
/// <remarks>
/// The envelope (<see cref="ReusableUnit"/>), the reference (<see cref="ReusableUnitRef"/>),
/// the immutable-versioned store, and the CP-lock cascade resolution are kind-agnostic.
/// Only the <em>payload</em> is kind-specific. Phase 1 wires the
/// <see cref="FormComponent"/> consumer end-to-end; <see cref="WorkflowSubgraph"/> is a
/// defined seam — a unit of that kind stores in the kind-agnostic envelope but is
/// rejected fail-closed when referenced from a form, until the workflow consumer lands.
/// </remarks>
public enum ReusableUnitKind
{
    /// <summary>A reusable form-item subtree + its per-field overlay map. Carried in
    /// <see cref="ReusableUnit.Component"/>.</summary>
    FormComponent = 0,

    /// <summary>A reusable workflow subgraph (nodes + edges). The Phase-1 seam: the
    /// envelope + reference + store + cascade all accept this kind, but no concrete
    /// subgraph payload / resolver is built yet — referencing one from a form is
    /// rejected fail-closed (<see cref="ReusableUnitCodes.WorkflowSubgraphUnsupported"/>).</summary>
    WorkflowSubgraph = 1,
}

/// <summary>
/// The <b>D4 reuse-unit primitive</b> (ADR 0135 amendment 2026-07-01) — a reference-able,
/// immutable-versioned, tenant-scoped reusable fragment that a definition points at by
/// <c>(Id, Version)</c> (<b>reuse-by-reference, not copy</b>) and resolves through the
/// ADR 0129 per-property lock cascade, <b>CP-locked-by-default</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One primitive, both surfaces.</b> A unit is <see cref="ReusableUnitKind.FormComponent"/>
/// (a form-item subtree + field overlays) or <see cref="ReusableUnitKind.WorkflowSubgraph"/>
/// (a workflow subgraph). The envelope, the reference, the immutable-versioned store, and
/// the CP-lock cascade are shared; only <see cref="Component"/> is form-specific. This is
/// the "one reuse-by-reference model for both forms and workflows" the amendment names.
/// </para>
/// <para>
/// <b>Identity is the tuple (Id, Version).</b> A unit id is reusable across versions; each
/// version is a distinct, immutable record — exactly like <see cref="FormDefinition"/>. The
/// store indexes by id and returns the highest <see cref="Version"/> at
/// <see cref="FormDefinitionStatus.Published"/> for a <see cref="ReusableUnitRef"/> that
/// tracks the latest published version (the propagation channel), or the exact version for
/// a pinned reference.
/// </para>
/// <para>
/// <b>CP-locked-by-default (Phase 1).</b> Every property the unit provides is LOCKED at the
/// reference site: the consuming definition cannot override it. There is no override channel
/// on <see cref="ReusableUnitRef"/> in this phase — the per-unit AP-override endpoint is
/// deferred behind three named kill-triggers. "Shared-locked" is not a rival mechanism; it
/// IS the all-CP-locked endpoint of the one cascade.
/// </para>
/// </remarks>
/// <param name="Id">Unit id (reusable across versions; the <c>(Tenant, Id, Version)</c>
/// tuple is unique).</param>
/// <param name="Version">Semantic version of this immutable revision.</param>
/// <param name="Status">Lifecycle status (shared with <see cref="FormDefinition"/>: only
/// <see cref="FormDefinitionStatus.Published"/> revisions are selected by a latest-published
/// reference).</param>
/// <param name="Tenant">Owning tenant; the store enforces isolation on all lookups.</param>
/// <param name="Kind">Form-component or workflow-subgraph discriminator.</param>
/// <param name="Owner">Authoring principal (use <see cref="IdentityRef.System"/> for seeds).</param>
/// <param name="Component">The form-component payload — non-null iff
/// <see cref="Kind"/> is <see cref="ReusableUnitKind.FormComponent"/>. Validation rejects a
/// FormComponent unit without a body, or a WorkflowSubgraph unit that carries one
/// (<see cref="ReusableUnitCodes.WrongKindBody"/>).</param>
/// <param name="Title">Optional localized unit title (rendered as the reference-site
/// container heading when a form references this unit).</param>
/// <param name="CreatedAt">UTC timestamp at which this revision was first registered.</param>
/// <param name="UpdatedAt">UTC timestamp at which this revision's status was last
/// transitioned. Equals <see cref="CreatedAt"/> on a freshly-registered revision.</param>
public sealed record ReusableUnit(
    ReusableUnitId Id,
    SemanticVersion Version,
    FormDefinitionStatus Status,
    TenantId Tenant,
    ReusableUnitKind Kind,
    IdentityRef Owner,
    FormComponentBody? Component,
    InternationalizedText? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The <see cref="ReusableUnitKind.FormComponent"/> payload — a reusable form-item subtree
/// plus the per-field overlay map for the fields that subtree references.
/// </summary>
/// <remarks>
/// Structurally a fragment of a <see cref="FormDefinition"/>: the same recursive
/// <see cref="FormItem"/> tree (Field / Group / Collection) drawing its per-field
/// label / control-hint / PII classification from the same shape of
/// <see cref="FieldOverlay"/> map as a top-level definition. A unit body may NOT itself
/// contain <see cref="FormItemKind.Reference"/> nodes in Phase 1 (unit-composing-unit is
/// deferred; a nested reference is rejected at registration).
/// </remarks>
/// <param name="Items">The reusable item subtree (≥1 item). Field items reference keys in
/// <see cref="Fields"/>.</param>
/// <param name="Fields">Per-field overlay map for the fields the subtree references, keyed
/// by field name — the properties that resolve CP-locked at every reference site.</param>
public sealed record FormComponentBody(
    IReadOnlyList<FormItem> Items,
    IReadOnlyDictionary<string, FieldOverlay> Fields);
