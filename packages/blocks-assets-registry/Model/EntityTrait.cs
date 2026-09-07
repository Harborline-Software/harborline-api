namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// Composable capabilities a type declares (annex §3.1, D-L). Traits <b>compose</b>
/// (a bedroom is <see cref="Container"/> + <see cref="Maintainable"/>; a water heater is
/// <see cref="Maintainable"/> + <see cref="Movable"/>) rather than being expressed by a
/// subclass hierarchy — this is what lets ONE entity model replace the Rev-2 places/assets
/// split without multiplying record families.
/// </summary>
[Flags]
public enum EntityTrait
{
    /// <summary>No traits. Not a valid persisted type — every type declares at least one.</summary>
    None = 0,

    /// <summary>
    /// May contain children in the spatial tree via the <see cref="RelationshipKind.Contains"/>
    /// edge (cycle-guarded, depth-bounded). Examples: property, building, unit, bedroom,
    /// hallway, shed.
    /// </summary>
    Container = 1 << 0,

    /// <summary>
    /// Carries condition ratings, inspections, deficiencies/work-orders, and capital-planning
    /// (expected useful life + replacement cost). Examples: furnace, roof, window, bedroom,
    /// cabinet, faucet.
    /// </summary>
    Maintainable = 1 << 1,

    /// <summary>
    /// Location is expressed via a <b>dated</b> <see cref="RelationshipKind.LocatedAt"/> edge
    /// (since/until → move history). Absence of this trait means <i>fixed</i> — structural
    /// position via containment, no dated edge needed (a window is part of its room). Examples of
    /// movable: water heater, appliance, vehicle.
    /// </summary>
    Movable = 1 << 2,
}
