namespace Harborline.Api.Foundation.Import.Outcomes;

/// <summary>
/// The three happy-path per-record outcome markers for the ERPNext importer
/// (ADR 0100 C2). Drives the migration report's happy-path counts.
/// </summary>
/// <remarks>
/// <para>
/// This enum deliberately has <b>no <c>Rejected</c> member</b> (ADR 0100 OQ-A
/// ruling). The reject is a first-class arm of the
/// <see cref="ImportOutcome{T}"/> discriminated union
/// (<see cref="ImportOutcome{T}.Rejected"/>), NOT an enum value — adding a
/// <c>Rejected</c> member would silently weaken exhaustive <c>switch</c>
/// statements that lack a <c>default</c>.
/// </para>
/// <para>
/// The enum is the canonical replacement for the per-cluster
/// <c>Harborline.Api.Blocks.*.Migration.ImportAction</c> copies (the D7 duplication
/// the contract collapses to this one owning package). The A-units decide
/// per cluster whether to retire the legacy enum or retain it as a pure
/// happy-path marker projected from the non-reject arms via
/// <see cref="ImportOutcome{T}.Action"/>.
/// </para>
/// </remarks>
public enum ImportAction
{
    /// <summary>The source record had no prior import; a new local record was created.</summary>
    Inserted,

    /// <summary>An existing local record was updated to match a newer source version.</summary>
    Updated,

    /// <summary>The source record was already present at the same or newer version — no change.</summary>
    Skipped,
}
