using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.Foundation.Packs.Export;

/// <summary>
/// A compose-time export request: the manifest header fields, the content sources to package, the
/// single-level dependencies, the declared capability requirements, and the signing
/// <see cref="Epoch"/>.
/// </summary>
/// <param name="Key">The pack key (ADR 0129 D1).</param>
/// <param name="Version">The pack's pinned semantic version.</param>
/// <param name="Name">Human-readable name. Export strips C0/C1 and explicit bidi formatting controls,
/// then visibly ellipsizes beyond <see cref="PackCardDisplayText.NameOrTitleCharacterLimit"/> Unicode
/// text elements.</param>
/// <param name="Description">Human-readable description. Export strips C0/C1 and explicit bidi
/// formatting controls, then visibly ellipsizes beyond
/// <see cref="PackCardDisplayText.FreeTextOrDescriptionCharacterLimit"/> Unicode text elements.</param>
/// <param name="ScopeTier">The pack scope-tier.</param>
/// <param name="Contents">The content sources to canonicalize + package.</param>
/// <param name="Dependencies">Single-level declared dependencies (A9).</param>
/// <param name="CapabilityRequirements">Declared engine-capability requirements.</param>
/// <param name="Epoch">The signing epoch bound into the signature (ADR 0126 D4).</param>
/// <param name="RenamedFrom">Optional S-10 key-stability map (new key ← old key) for an upgrade that
/// renames a content key, so the install engine re-attaches overrides across the rename.</param>
/// <param name="ProviderSlot">Optional three-tier slotting signal: a category-provider pack declares
/// the exclusive category slot it fills (activate-exclusive); a domain-block / capability-plugin
/// leaves it null. Flows into the signed <see cref="PackManifest.ProviderSlot"/>.</param>
/// <param name="Dcp">The Domain Compliance Profile the pack declares (ADR 0145). The export gate REQUIRES
/// it: a <c>null</c> DCP is refused fail-closed (<c>pack.dcp.missing</c>) — a pack MUST carry a valid DCP.
/// The route-layer grandfather-default attaches a <c>general</c> DCP when a client omits one (compatibility
/// plan); the exporter itself never defaults — presence is the ceremony's own gate.</param>
/// <param name="DisplayName">Optional publisher-signed marketplace-card display name, bounded as a
/// name/title by <see cref="PackCardDisplayText"/> at export.</param>
/// <param name="Tagline">Optional publisher-signed marketplace-card free text, bounded as a
/// description by <see cref="PackCardDisplayText"/> at export.</param>
/// <param name="Category">Optional publisher-signed marketplace-card category, bounded as a
/// name/title by <see cref="PackCardDisplayText"/> at export.</param>
/// <param name="IconRef">Optional content address of the marketplace-card icon blob.</param>
/// <param name="Exposes">Definition keys this pack exposes to other packs. Ticket 396: a pack
/// reaching into a definition absent from this list is refused at activation.</param>
/// <param name="InterfaceVersion">The exact interface version this pack exposes, as the integer in
/// the ADR 0006 <c>pack-key@integer</c> requirement spelling. Ticket 396 matches it exactly rather
/// than by range, because a range admits a consumer never tested against the active interface.</param>
public sealed record PackExportRequest(
    string Key,
    string Version,
    string Name,
    string Description,
    PackScopeTier ScopeTier,
    IReadOnlyList<PackContentSource> Contents,
    IReadOnlyList<PackDependencyRef> Dependencies,
    IReadOnlyList<string> CapabilityRequirements,
    long Epoch,
    IReadOnlyList<PackContentRename>? RenamedFrom = null,
    string? ProviderSlot = null,
    DomainComplianceProfile? Dcp = null,
    string? DisplayName = null,
    string? Tagline = null,
    string? Category = null,
    Cid? IconRef = null,
    IReadOnlyList<string>? Exposes = null,
    int? InterfaceVersion = null);

/// <summary>
/// The outcome of an export. On success, both the structured <see cref="File"/> and the encoded
/// single-file <see cref="FileBytes"/> are present. On a validation refusal, <see cref="Succeeded"/>
/// is <c>false</c>, both are <c>null</c>, and <see cref="Validation"/> carries the findings — export
/// FAIL-CLOSED refuses to sign an invalid pack (you cannot export instance data / deps-of-deps /
/// unpinned refs).
/// </summary>
/// <param name="Succeeded">Whether a signed pack file was produced.</param>
/// <param name="File">The structured signed pack file, or <c>null</c> on refusal.</param>
/// <param name="FileBytes">The encoded single-file bytes (the sneakernet artifact), or
/// <c>null</c>.</param>
/// <param name="Validation">The validation result (always populated).</param>
public sealed record PackExportOutcome(
    bool Succeeded,
    PackFile? File,
    byte[]? FileBytes,
    PackValidationResult Validation);
