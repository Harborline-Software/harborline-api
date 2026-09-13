using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// The pack manifest — the ADR 0129 D1 pack unit (architect fold A1/A4). It uses <see cref="Key"/>
/// (NOT <c>id</c>), carries content-addressed <see cref="Contents"/> (the new field over the
/// ancestral ADR 0007 bundle-manifest shape), and reuses ADR 0007's envelope CONVENTIONS (semver
/// <see cref="Version"/>, <c>CanonicalJson</c> encoding per 0007-A1.2) — but it is NOT a
/// <c>BusinessCaseBundleManifest</c> (a different granularity/layer, A4).
/// </summary>
/// <remarks>
/// <para>
/// The manifest is the SIGNED subject's payload (via <c>PackSignatureSubject</c>), so the signature
/// covers every field — including each <see cref="PackContentRef.ContentAddress"/> in
/// <see cref="Contents"/>. That is the merkle binding (S-14): the manifest is the root that lists
/// the leaf content-addresses, and signing it makes inner-item substitution structurally detectable.
/// </para>
/// <para>
/// B-1a validates this manifest for completeness (every ref pinned + resolvable to a carried item),
/// single-level dependencies (A9), and value-level PII hygiene (S-12 floor). It does NOT resolve the
/// composition DAG, apply overrides, or install — those are B-1b / B-2.
/// </para>
/// </remarks>
/// <param name="Key">The pack key (ADR 0129 D1 — <c>key</c>, never a mutable <c>id</c>).</param>
/// <param name="Version">The pack's PINNED semantic version. A pack version is immutable once
/// exported (ADR 0011).</param>
/// <param name="Name">Human-readable pack name.</param>
/// <param name="Description">Human-readable pack description.</param>
/// <param name="ScopeTier">The pack's scope-tier (ADR 0129 D1).</param>
/// <param name="Contents">The content-addressed, version-pinned inner items (the merkle leaves).</param>
/// <param name="Dependencies">Single-level declared dependencies (bounded 0129 D3 slice; A9).</param>
/// <param name="CapabilityRequirements">The engine capabilities/features this pack declares it
/// needs (declared, not resolved here).</param>
/// <param name="RenamedFrom">The S-10 key-stability migration map. When an upgrade renames a stable
/// content <see cref="PackContentRef.Key"/>, it MUST declare the rename here (new key ← old key) so
/// the install engine (B-1b) re-attaches a tenant override that targeted the OLD key to the NEW key,
/// instead of silently orphaning it (a silent revert to pack behaviour). The map is INSIDE the signed
/// manifest, so a malicious upgrade cannot forge a rename to hijack an override. <c>null</c>/empty ⇒
/// no renames (the common case).</param>
/// <param name="ProviderSlot">The three-tier slotting classification signal (ADR 0129 D4 / 0125): a
/// <b>category-provider</b> pack declares the exclusive category slot it fills (e.g. <c>"payments"</c>),
/// so activation can enforce one-active-provider-per-category (the "swap, not stack" tier). A
/// <b>domain-block</b> (side-by-side union, e.g. the General pack) or a <b>capability-plugin</b>
/// (orthogonal add) leaves this <c>null</c> — the common case, no exclusivity. The value is INSIDE the
/// signed manifest, so a pack's provider identity is signer-controlled + unforgeable, and existing
/// unslotted packs stay valid (null ⇒ no exclusivity constraint).</param>
/// <param name="Dcp">The pack's Domain Compliance Profile leaf (ADR 0145 D3.4) — a DISTINCT, signed
/// content-address reference to the DCP (NOT loose manifest fields, NOT part of <see cref="Contents"/>).
/// Because it lives inside the signed manifest, signing merkle-binds the DCP, and because it is its own
/// referenceable leaf the DCP stays separately-attributable (the solo→multi RACI fan-out is a permission
/// change, not a re-architecture). <c>null</c> ⇒ a legacy/pre-DCP pack; the export gate now always attaches
/// one (ADR 0145 "a pack MUST carry a valid DCP"), so a freshly-exported pack is never null here.</param>
/// <param name="ContentReferences">The declared, signed, content-grain cross-app reference edges (app-layer
/// design note §6.3, slice G2) — "this pack's Vehicle type builds on Core Records' Property type". Emitted by
/// the Composer from parsed content, validated fail-closed at install (a reference to a not-installed app
/// refuses, naming it), and persisted on <c>InstalledPack</c> so the graph's class-3 edge survives without a
/// body re-parse. <b>ADDITIVE.</b> <c>null</c> ⇒ a pre-G2 / dependency-free pack; because the property is
/// omitted-when-null from the signed canonical form (<see cref="JsonIgnoreAttribute"/>
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/>), a pack that carries no edges serializes BYTE-IDENTICALLY
/// to a pre-G2 manifest — so every existing signature still verifies unchanged (cerebrum 2026-07-07: adding a
/// field to a signed projection type is additive-only when absent-omitted).</param>
/// <param name="DisplayName">Optional marketplace-card display name asserted by the publisher.</param>
/// <param name="Tagline">Optional marketplace-card tagline asserted by the publisher.</param>
/// <param name="Category">Optional marketplace-card category asserted by the publisher.</param>
/// <param name="IconRef">Optional content address of the marketplace-card icon blob. The CID is inside
/// the signed manifest, so substituting a different icon changes the signed merkle leaf.</param>
/// <param name="Exposes">Definition keys this pack exposes to other packs. Inside the signed
/// manifest, so the claim cannot be widened after signing. Ticket 396.</param>
/// <param name="InterfaceVersion">The exact interface version this pack exposes, as the integer in
/// the ADR 0006 <c>pack-key@integer</c> requirement spelling. Signed for the same reason, and
/// matched exactly rather than by range: a range admits a consumer never tested against the
/// interface actually active. Ticket 396.</param>
/// <remarks>
/// <para>
/// <see cref="Name"/>, <see cref="Description"/>, <see cref="DisplayName"/>, <see cref="Tagline"/>,
/// and <see cref="Category"/> are untrusted display text even when the publisher signature verifies.
/// <see cref="PackCardDisplayText"/> defines the publisher-export bounds and the mandatory consumer
/// escaping and bidi-isolation projection. Render name/title values through
/// <see cref="PackCardDisplayText.PrepareNameOrTitleForRender"/> and free text through
/// <see cref="PackCardDisplayText.PrepareFreeTextOrDescriptionForRender"/> without rewriting this
/// verified signed model.
/// </para>
/// <para>
/// These bounds apply only when exporting a new pack. Readers and verifiers remain tolerant of older
/// signed manifests whose card strings predate the bounds.
/// </para>
/// </remarks>
public sealed record PackManifest(
    string Key,
    string Version,
    string Name,
    string Description,
    PackScopeTier ScopeTier,
    IReadOnlyList<PackContentRef> Contents,
    IReadOnlyList<PackDependencyRef> Dependencies,
    IReadOnlyList<string> CapabilityRequirements,
    IReadOnlyList<PackContentRename>? RenamedFrom = null,
    string? ProviderSlot = null,
    PackDcpRef? Dcp = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PackContentReferenceEdge>? ContentReferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Tagline = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Category = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Cid? IconRef = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Exposes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? InterfaceVersion = null);

/// <summary>
/// One entry in a manifest's S-10 <see cref="PackManifest.RenamedFrom"/> key-stability map: the
/// upgrade declares that the content now keyed <see cref="NewKey"/> was keyed <see cref="OldKey"/> in
/// a prior version, so a tenant override targeting <see cref="OldKey"/> re-attaches to
/// <see cref="NewKey"/> at upgrade instead of orphaning (S-10 — a rename cannot silently drop an
/// override).
/// </summary>
/// <param name="NewKey">The content key in THIS (new) pack version.</param>
/// <param name="OldKey">The content key in the prior version the override may have targeted.</param>
public sealed record PackContentRename(string NewKey, string OldKey);
