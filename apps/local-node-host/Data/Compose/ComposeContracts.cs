using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.LocalNodeHost.Data.Compose;

/// <summary>The domain request to <see cref="ComposeCeremony.ComposeAsync"/>. v1 selects asset types and
/// published form definitions; other content kinds remain deferred.</summary>
/// <param name="ComposeId">Reuse an existing draft id to REPLACE its snapshot (re-compose), or <c>null</c>
/// to start a fresh draft.</param>
/// <param name="Key">The pack key.</param>
/// <param name="Version">The pack version.</param>
/// <param name="Name">The pack display name (defaults to <see cref="Key"/> when blank).</param>
/// <param name="Description">The pack description.</param>
/// <param name="ScopeTier">The pack scope tier.</param>
/// <param name="TypeIds">The selected asset-type ids to snapshot from the live registry.</param>
/// <param name="Dcp">The declared DCP (the route defaults a missing one to the <c>general</c> grandfather).</param>
/// <param name="Dependencies">Declared single-level dependencies (ADR 0129 D3).</param>
/// <param name="CapabilityRequirements">Declared capability requirements.</param>
/// <param name="FormIds">Published form-definition ids to snapshot at their current published version.</param>
public sealed record ComposeRequest(
    string? ComposeId,
    string Key,
    string Version,
    string? Name,
    string? Description,
    PackScopeTier ScopeTier,
    IReadOnlyList<string> TypeIds,
    DomainComplianceProfile? Dcp,
    IReadOnlyList<PackDependencyRef>? Dependencies = null,
    IReadOnlyList<string>? CapabilityRequirements = null,
    IReadOnlyList<string>? FormIds = null);

/// <summary>
/// Stable, locale-independent compose-time WARNING codes (mirrors <c>PackValidationCodes</c>). A warning is
/// advisory — it never fails the compose (unlike a <c>PackValidationError</c>); it tells the author that the
/// snapshot lost something the projection cannot carry. The client localizes off the CODE + <c>Params</c>
/// (the fleet rule: "stable codes + params, never English literals").
/// </summary>
public static class ComposeWarningCodes
{
    /// <summary>The projected asset type carried form-binding field(s) the pinned
    /// <c>AssetTypeDefinition</c> content shape cannot represent, so the pack will NOT carry them
    /// (compose-time detection for the B-2a lossy-projection gap, #141). <c>Params["fields"]</c> is the
    /// comma-joined list of dropped field tokens (e.g. <c>propertyFormBinding,inspectionFormBindings</c>).</summary>
    public const string ProjectionLossyFormBinding = "pack.compose.projection.lossy.form_binding";
}

/// <summary>
/// One advisory compose-time finding — a stable, localizable <see cref="Code"/>, an optional
/// <see cref="Target"/> (the offending asset-type id, so the client can anchor it), and structured
/// <see cref="Params"/> the client interpolates into the localized message. Carries NO English (the doctrine:
/// the client renders the message from a catalog template keyed by <see cref="Code"/>). Distinct from
/// <see cref="PackValidationError"/>: a warning is non-fatal — compose still succeeds.
/// </summary>
/// <param name="Code">The stable, localizable code (one of <see cref="ComposeWarningCodes"/>).</param>
/// <param name="Target">The offending asset-type id, or <c>null</c> for a pack-level warning.</param>
/// <param name="Params">The localization inputs the client interpolates (never English display text).</param>
public sealed record ComposeWarning(string Code, string? Target, IReadOnlyDictionary<string, string> Params);

/// <summary>The discriminated outcome status shared by the compose/affirm/export steps.</summary>
public enum ComposeStatus
{
    /// <summary>The step succeeded.</summary>
    Ok,

    /// <summary>No draft with that id is visible to this tenant.</summary>
    NotFound,

    /// <summary>The composed content / DCP failed validation (see the carried errors).</summary>
    ValidationFailed,

    /// <summary>The affirmed hash does not bind the current snapshot (S-1 — "sign what you inspected").</summary>
    SnapshotMismatch,

    /// <summary>Export was attempted before the human PII-review affirmation (fail-closed, FU-1).</summary>
    AffirmationRequired,
}

/// <summary>The outcome of <see cref="ComposeCeremony.ComposeAsync"/> / <see cref="ComposeCeremony.Affirm"/>.</summary>
public sealed record ComposeOutcome(
    ComposeStatus Status,
    DraftComposition? Draft,
    IReadOnlyList<PackValidationError> Errors)
{
    /// <summary>A success carrying the (possibly newly-affirmed) draft.</summary>
    public static ComposeOutcome Ok(DraftComposition draft) =>
        new(ComposeStatus.Ok, draft, Array.Empty<PackValidationError>());

    /// <summary>The draft is unknown to this tenant.</summary>
    public static ComposeOutcome NotFound() =>
        new(ComposeStatus.NotFound, null, Array.Empty<PackValidationError>());

    /// <summary>The compose request failed validation.</summary>
    public static ComposeOutcome Invalid(IReadOnlyList<PackValidationError> errors) =>
        new(ComposeStatus.ValidationFailed, null, errors);

    /// <summary>The affirmed hash does not match the draft's current snapshot.</summary>
    public static ComposeOutcome Mismatch(DraftComposition draft) =>
        new(ComposeStatus.SnapshotMismatch, draft, Array.Empty<PackValidationError>());
}

/// <summary>The outcome of <see cref="ComposeCeremony.ExportAsync"/>.</summary>
public sealed record ComposeExportOutcome(
    ComposeStatus Status,
    byte[]? FileBytes,
    IReadOnlyList<PackValidationError> Errors,
    string? FileName = null)
{
    /// <summary>A signed pack file was produced (with its sneakernet file name).</summary>
    public static ComposeExportOutcome Exported(byte[] fileBytes, string fileName) =>
        new(ComposeStatus.Ok, fileBytes, Array.Empty<PackValidationError>(), fileName);

    /// <summary>The draft is unknown to this tenant.</summary>
    public static ComposeExportOutcome NotFound() =>
        new(ComposeStatus.NotFound, null, Array.Empty<PackValidationError>());

    /// <summary>Export was attempted before the human PII-review affirmation.</summary>
    public static ComposeExportOutcome AffirmationRequired() =>
        new(ComposeStatus.AffirmationRequired, null, Array.Empty<PackValidationError>());

    /// <summary>The affirmation no longer binds the current snapshot (re-composed after affirming).</summary>
    public static ComposeExportOutcome Mismatch() =>
        new(ComposeStatus.SnapshotMismatch, null, Array.Empty<PackValidationError>());

    /// <summary>The DCP gate / completeness validator refused the pack (carries the codes).</summary>
    public static ComposeExportOutcome ValidationFailed(IReadOnlyList<PackValidationError> errors) =>
        new(ComposeStatus.ValidationFailed, null, errors);
}
