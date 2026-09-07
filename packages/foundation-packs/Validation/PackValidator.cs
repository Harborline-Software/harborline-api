using System.Text.RegularExpressions;

using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Validation;

/// <summary>
/// The fail-closed pack validator (design §2.2). Confirms completeness (every referenced definition
/// exists + is version-pinned), single-level dependencies (architect fold A9 — deps-of-deps
/// REFUSED), capability declarations, and the value-level PII floor (S-12). Emits STABLE localizable
/// codes; the client localizes off the code, never the English message.
/// </summary>
/// <remarks>
/// The validator is content-shape agnostic — it treats each item body as opaque canonical JSON and
/// enforces STRUCTURAL invariants over the manifest + the value-level PII floor over the bodies. It
/// does NOT resolve the composition DAG, apply overrides, or run ADR 0143 admission — those are the
/// install engine's (B-1b) obligations.
/// </remarks>
public sealed class PackValidator
{
    // A PINNED version: exactly major.minor.patch with an optional prerelease/build. Rejects ranges
    // / wildcards (^1.0.0, 1.x, *) — a pack references definitions by pinned version, never a range.
    private static readonly Regex PinnedVersion = new(
        @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private readonly PackContentPiiScanner _piiScanner;

    /// <summary>Constructs a validator over the given PII scanner.</summary>
    public PackValidator(PackContentPiiScanner piiScanner)
    {
        _piiScanner = piiScanner ?? throw new ArgumentNullException(nameof(piiScanner));
    }

    /// <summary>
    /// Validates <paramref name="manifest"/> against the carried <paramref name="items"/>. Returns
    /// <see cref="PackValidationResult.Valid"/> only when every invariant holds; otherwise a failing
    /// result carrying every finding (all checks run — the caller sees the full picture, not just
    /// the first error).
    /// </summary>
    public PackValidationResult Validate(PackManifest manifest, IReadOnlyList<PackContentItem> items)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(items);

        var errors = new List<PackValidationError>();

        // ── Manifest header ────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(manifest.Key))
        {
            errors.Add(new(PackValidationCodes.ManifestKeyMissing, null, "manifest key is required."));
        }
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add(new(PackValidationCodes.ManifestNameMissing, null, "manifest name is required."));
        }
        if (!IsPinned(manifest.Version))
        {
            errors.Add(new(PackValidationCodes.ManifestVersionUnpinned, manifest.Key,
                $"manifest version '{manifest.Version}' is not a pinned semantic version."));
        }

        // ── Content refs ↔ carried items ───────────────────────────────────────
        var itemsByKey = new Dictionary<string, PackContentItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // A duplicate item key is caught on the ref side below; last-wins here is fine for lookup.
            itemsByKey[item.Key] = item;
        }

        var seenRefKeys = new HashSet<string>(StringComparer.Ordinal);
        var referencedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in manifest.Contents)
        {
            if (string.IsNullOrWhiteSpace(reference.Key))
            {
                errors.Add(new(PackValidationCodes.ContentKeyMissing, null, "a content ref has a blank key."));
                continue;
            }
            referencedKeys.Add(reference.Key);
            if (!seenRefKeys.Add(reference.Key))
            {
                errors.Add(new(PackValidationCodes.ContentKeyDuplicate, reference.Key,
                    $"content key '{reference.Key}' is declared more than once."));
            }
            if (!IsPinned(reference.Version))
            {
                errors.Add(new(PackValidationCodes.ContentVersionUnpinned, reference.Key,
                    $"content '{reference.Key}' version '{reference.Version}' is not pinned."));
            }
            if (!itemsByKey.TryGetValue(reference.Key, out var item))
            {
                errors.Add(new(PackValidationCodes.ContentItemMissing, reference.Key,
                    $"content ref '{reference.Key}' has no carried item."));
                continue;
            }
            if (!item.ContentAddress.Equals(reference.ContentAddress))
            {
                errors.Add(new(PackValidationCodes.ContentAddressMismatch, reference.Key,
                    $"content '{reference.Key}' address does not match the carried item."));
            }
        }

        // Orphan payloads — a carried item nothing references (a smuggled extra).
        foreach (var item in items)
        {
            if (!referencedKeys.Contains(item.Key))
            {
                errors.Add(new(PackValidationCodes.ContentItemOrphan, item.Key,
                    $"carried item '{item.Key}' is not referenced by the manifest."));
            }
        }

        // ── Dependencies (single-level only — A9) ──────────────────────────────
        foreach (var dependency in manifest.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.Key) || !IsPinned(dependency.Version))
            {
                errors.Add(new(PackValidationCodes.DependencyUnpinned, dependency.Key,
                    $"dependency '{dependency.Key}' must have a key and a pinned version."));
            }
            if (dependency.DeclaredDependencyKeys.Count > 0)
            {
                errors.Add(new(PackValidationCodes.TransitiveDependency, dependency.Key,
                    $"dependency '{dependency.Key}' itself declares dependencies " +
                    $"({string.Join(", ", dependency.DeclaredDependencyKeys)}) — v1 supports single-level deps only."));
            }
        }

        // ── Capability requirements ────────────────────────────────────────────
        foreach (var capability in manifest.CapabilityRequirements)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                errors.Add(new(PackValidationCodes.CapabilityRequirementBlank, manifest.Key,
                    "a declared capability requirement is blank."));
            }
        }

        // ── Value-level PII / instance-data floor (S-12) ───────────────────────
        foreach (var item in items)
        {
            errors.AddRange(_piiScanner.Scan(item));
        }

        return errors.Count == 0 ? PackValidationResult.Valid : PackValidationResult.Invalid(errors);
    }

    private static bool IsPinned(string? version)
        => !string.IsNullOrWhiteSpace(version) && PinnedVersion.IsMatch(version);
}
