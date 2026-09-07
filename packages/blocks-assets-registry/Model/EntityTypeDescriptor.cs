using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// The descriptive payload of an entity type (annex §3.2), shared by the immutable
/// <see cref="EntityTypeSeed"/> template and the tenant-scoped <see cref="EntityType"/> row.
/// Factoring it out keeps a tenant override byte-identical to its seed except for the fields the
/// tenant actually changed, and keeps the immutable-seed / mutable-override distinction at the
/// row level (F2 invariant 1) rather than duplicating attributes.
/// </summary>
/// <param name="DisplayName">Human-friendly type name (e.g. "Condenser", "Water heater").</param>
/// <param name="Traits">Composable capabilities (container / maintainable / movable). At least one.</param>
/// <param name="ParentType">
/// Optional single-parent type for defaulting (D-C: Equipment → HVAC equipment → Condenser).
/// Bindings inherit down; children override. Depth is bounded by the registry.
/// </param>
/// <param name="PropertyFormBinding">
/// Optional form that defines the type's dynamic attributes (content-addressed + pinned
/// version). Per-entity values pin their own version via <see cref="RegistryEntity.PropertyForm"/>.
/// </param>
/// <param name="Disciplines">The disciplines that inspect this type (annex §3.3).</param>
/// <param name="InspectionFormBindings">
/// Inspection-form bindings keyed by discipline — the (type × discipline) binding (annex §3.2):
/// the electrical condenser form ≠ the HVAC condenser form. One physical asset, several
/// discipline bindings; each inspector sees only their own.
/// </param>
/// <param name="ExpectedUsefulLifeYears">
/// Type-level capital-planning default (D-N), cascade-overridable. <b>Read-side operations
/// planning only</b> — never ledger depreciation, never a posting write path.
/// </param>
/// <param name="TypicalReplacementCost">
/// Type-level typical replacement cost (D-N), typed <see cref="Money"/> from the start.
/// <b>Read-side planning only</b>; distinct from the financial <c>DepreciationSchedule</c>.
/// </param>
/// <param name="ConditionScaleMax">
/// Optional type-level default condition-scale maximum (annex §3.8, D-M). When set, it is the
/// number of points on this type's condition scale (worst = 1 … best = <c>ConditionScaleMax</c>),
/// and MUST be at least 2 (matching <see cref="ConditionRating"/>). Null means "use the platform
/// default" (<see cref="ConditionRating.DefaultScaleMax"/> = 5). This is a cascade-overridable
/// default the Type Manager configures; per-assessment ratings still carry their own
/// <see cref="ConditionRating.ScaleMax"/> so a historic grade stays interpretable if the type's
/// scale later changes (like the property-form version pin, D-D).
/// </param>
public sealed record EntityTypeDescriptor(
    string DisplayName,
    EntityTrait Traits,
    EntityTypeId? ParentType = null,
    FormBindingRef? PropertyFormBinding = null,
    IReadOnlyList<DisciplineTag>? Disciplines = null,
    IReadOnlyDictionary<DisciplineTag, FormBindingRef>? InspectionFormBindings = null,
    int? ExpectedUsefulLifeYears = null,
    Money? TypicalReplacementCost = null,
    int? ConditionScaleMax = null)
{
    /// <summary>The applicable disciplines (never null; empty when none declared).</summary>
    public IReadOnlyList<DisciplineTag> Disciplines { get; init; } =
        Disciplines ?? Array.Empty<DisciplineTag>();

    /// <summary>The (type × discipline) inspection-form bindings (never null; empty when none).</summary>
    public IReadOnlyDictionary<DisciplineTag, FormBindingRef> InspectionFormBindings { get; init; } =
        InspectionFormBindings ?? new Dictionary<DisciplineTag, FormBindingRef>();
}
