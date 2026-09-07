namespace Harborline.Api.Foundation.BusinessLogic.Enums;

// Ticket 205 slice 6: AccessMode, AuthorizationAction and RuleOutcome went with the business-object
// property engine they described (packages/foundation/BusinessLogic outside this folder). What is
// left is read by the allocation-scheduler models and their Blazor component.

/// <summary>
/// Lifecycle state of a scenario allocation set.
/// Used by the AllocationScheduler and any component that supports scenario planning.
/// </summary>
public enum ScenarioStatus
{
    /// <summary>Being edited; visible only to the creator.</summary>
    Draft,

    /// <summary>Open for team review.</summary>
    Shared,

    /// <summary>Frozen for stakeholder sign-off; no further edits.</summary>
    Approved,

    /// <summary>Merged into the baseline and archived.</summary>
    Promoted,

    /// <summary>Archived without promotion.</summary>
    Rejected
}

/// <summary>The category of an allocation set — baseline or divergent scenario.</summary>
public enum AllocationSetType
{
    /// <summary>The committed plan. Only one active baseline per project.</summary>
    Baseline,

    /// <summary>A divergent branch of allocations that overrides the baseline.</summary>
    Scenario
}
