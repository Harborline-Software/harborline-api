namespace Harborline.Api.Foundation.Packs.Validation;

/// <summary>
/// Stable, locale-independent validation CODES. The fleet rule is "validation errors are stable
/// codes + params, not English literals" — the client localizes off the CODE. The English
/// <see cref="PackValidationError.Message"/> is a developer aid only.
/// </summary>
public static class PackValidationCodes
{
    // ── Completeness / pinning ─────────────────────────────────────────────────
    /// <summary>The manifest key is missing/blank.</summary>
    public const string ManifestKeyMissing = "pack.validation.manifest.key.missing";
    /// <summary>The manifest name is missing/blank.</summary>
    public const string ManifestNameMissing = "pack.validation.manifest.name.missing";
    /// <summary>The manifest version is missing or not a valid pinned semantic version.</summary>
    public const string ManifestVersionUnpinned = "pack.validation.manifest.version.unpinned";
    /// <summary>A content ref has a missing/blank key.</summary>
    public const string ContentKeyMissing = "pack.validation.content.key.missing";
    /// <summary>Two content items share the same key.</summary>
    public const string ContentKeyDuplicate = "pack.validation.content.key.duplicate";
    /// <summary>A content ref's version is missing or not a valid pinned semantic version.</summary>
    public const string ContentVersionUnpinned = "pack.validation.content.version.unpinned";
    /// <summary>A manifest content ref has no matching carried content item.</summary>
    public const string ContentItemMissing = "pack.validation.content.item.missing";
    /// <summary>A carried content item is not referenced by the manifest (an orphan payload).</summary>
    public const string ContentItemOrphan = "pack.validation.content.item.orphan";
    /// <summary>A content ref's declared address does not match the item's recomputed address.</summary>
    public const string ContentAddressMismatch = "pack.validation.content.address.mismatch";

    // ── Dependency / capability ────────────────────────────────────────────────
    /// <summary>A declared dependency has a missing/blank key or unpinned version.</summary>
    public const string DependencyUnpinned = "pack.validation.dependency.unpinned";
    /// <summary>A declared dependency itself declares dependencies (deps-of-deps) — single-level
    /// violated, fail-closed refuse (architect fold A9).</summary>
    public const string TransitiveDependency = "pack.validation.dependency.transitive";
    /// <summary>A declared capability requirement is blank.</summary>
    public const string CapabilityRequirementBlank = "pack.validation.capability.blank";

    // ── PII / instance-data floor (S-6/S-12) ───────────────────────────────────
    /// <summary>Content is shaped like captured instance data (submitted values / submissions).</summary>
    public const string ContentInstanceData = "pack.validation.content.pii.instance_data";
    /// <summary>Content references a concrete party by id/email rather than by role (S-12).</summary>
    public const string ContentConcretePartyRef = "pack.validation.content.pii.concrete_party_ref";
    /// <summary>Content embeds a concrete email address in a definition/default (S-12).</summary>
    public const string ContentEmbeddedEmail = "pack.validation.content.pii.embedded_email";
}

/// <summary>
/// One validation finding — a stable <see cref="Code"/>, an optional <see cref="Target"/> (the
/// offending content/dependency key or JSON path, so a client can anchor an inline error), and a
/// developer-facing English <see cref="Message"/>.
/// </summary>
/// <param name="Code">The stable, localizable code (one of <see cref="PackValidationCodes"/>).</param>
/// <param name="Target">The offending key / JSON path, or <c>null</c> for a manifest-level error.</param>
/// <param name="Message">Developer aid only — NOT for display; the client localizes off
/// <paramref name="Code"/>.</param>
public sealed record PackValidationError(string Code, string? Target, string Message);

/// <summary>
/// The outcome of validating a pack — <see cref="IsValid"/> plus the list of
/// <see cref="Errors"/> (empty when valid). Fail-closed: any error ⇒ the pack is refused (never
/// exported / never trusted for install).
/// </summary>
public sealed record PackValidationResult(bool IsValid, IReadOnlyList<PackValidationError> Errors)
{
    /// <summary>A passing result (no errors).</summary>
    public static PackValidationResult Valid { get; } = new(true, Array.Empty<PackValidationError>());

    /// <summary>Builds a failing result from a non-empty error list.</summary>
    public static PackValidationResult Invalid(IReadOnlyList<PackValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new PackValidationResult(errors.Count == 0, errors);
    }
}
