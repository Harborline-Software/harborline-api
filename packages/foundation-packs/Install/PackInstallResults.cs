using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>Stable, locale-independent install-engine codes (a client localizes off these).</summary>
public static class PackInstallCodes
{
    /// <summary>Verification did not return <c>Verified</c> — the pack is not admitted (S-7). The verdict's
    /// own detail codes carry the specifics (tamper / untrusted / epoch / unsigned).</summary>
    public const string RefusedNotVerified = "pack.install.refused.not_verified";

    /// <summary>The signer key + epoch is on the channel revocation list (S-11).</summary>
    public const string RefusedRevoked = "pack.install.refused.revoked";

    /// <summary>The version is below the installed watermark — a downgrade, refused by default (S-8).</summary>
    public const string RefusedDowngrade = "pack.install.refused.downgrade";

    /// <summary>The upgrade's seed lowers a safety floor below the watermark — refused by default (S-8).</summary>
    public const string RefusedFloorWeakened = "pack.install.refused.floor_weakened";

    /// <summary>An effecting (workflow) definition failed install-time ADR 0143 admission (S-9).</summary>
    public const string RefusedAdmission = "pack.install.refused.admission";

    /// <summary>The vouching trust scope may not seed this content (S-13 — structural; no v1 first-party
    /// scope is restricted, but the check exists so enabling a future scoped root is fail-closed).</summary>
    public const string RefusedScope = "pack.install.refused.scope";

    /// <summary>A declared content-grain cross-app reference (design note §6.3, slice G2) targets an app /
    /// contributable that is NOT installed — install refuses fail-closed, naming the missing app(s) in
    /// <see cref="PackInstallPreview.UnmetContentReferences"/>. Unlike an S-8 watermark hit this is NOT
    /// break-glass-overridable (a missing dependency cannot be waved through — install the required app
    /// first). Follows the localizable-code doctrine: the client renders the message from this code +
    /// the structured unmet references, never an English literal.</summary>
    public const string RefusedUnmetContentReference = "pack.install.refused.unmet_content_reference";

    /// <summary>A manifest-DECLARED pack dependency (ADR 0129 D3 single-level slice) does not resolve to
    /// an installed pack — either no version of the dependency key is installed, or every installed
    /// version is below the pinned version (per <see cref="PackVersion.Compare"/> semantics; S-8
    /// monotonic upgrades mean a NEWER installed version satisfies the pin). Ticket 152: a declared
    /// dependency that was never installed used to pass install silently — now a HARD fail-closed
    /// refusal naming the missing dependency in <see cref="PackInstallPreview.UnmetDependencies"/>
    /// (never break-glass-overridable; install the dependency first).</summary>
    public const string RefusedUnmetDependency = "pack.install.refused.unmet_dependency";

    /// <summary>A manifest-DECLARED dependency PIN does not parse as a pinned version
    /// (<see cref="PackVersion.IsWellFormed"/>). <see cref="PackVersion.Compare"/> degrades an
    /// unparseable segment to 0 — fail-closed for a candidate, but fail-OPEN for a pin (a pin of
    /// 0.0.0 is trivially satisfied by anything installed) — so the dependency check REFUSES a
    /// malformed pin outright with this distinct code instead of comparing it (ADR 0038).</summary>
    public const string RefusedMalformedDependencyPin =
        "pack.install.refused.malformed_dependency_pin";

    /// <summary>A signed definition envelope requires a capability this platform does not provide.</summary>
    public const string RefusedMissingPlatformCapability =
        "pack.install.refused.missing_platform_capability";

    /// <summary>A provided capability requires a newer platform version than the running build.</summary>
    public const string RefusedPlatformVersionFloor =
        "pack.install.refused.platform_version_floor";

    /// <summary>The pack carries a standards catalog, but this build has no projector for it.</summary>
    public const string RefusedUnsupportedStandardsCatalog =
        "pack.install.refused.unsupported_content_kind.standards_catalog";

    /// <summary>The pack carries cascade defaults, but this build has no seed projector for them.</summary>
    public const string RefusedUnsupportedCascadeDefaults =
        "pack.install.refused.unsupported_content_kind.cascade_defaults";

    /// <summary>The pack carries a terminology override, but this build has no seed projector for it.</summary>
    public const string RefusedUnsupportedTerminologyOverride =
        "pack.install.refused.unsupported_content_kind.terminology_override";

    /// <summary>A first install created a new seed layer (Draft).</summary>
    public const string Installed = "pack.install.installed";

    /// <summary>An upgrade installed a newer seed layer (Draft).</summary>
    public const string Upgraded = "pack.install.upgraded";

    /// <summary>Activation refused: the version is not installed (Draft/Inactive → Active on a missing
    /// version).</summary>
    public const string ActivateNotInstalled = "pack.install.activate.not_installed";

    /// <summary>Activation refused: activating this category-provider would occupy a category slot an
    /// already-ACTIVE provider holds (activate-exclusive, ADR 0129 D4). Install stays additive (S-2); the
    /// operator deactivates the incumbent first, so no provider is silently swapped out.</summary>
    public const string ActivateProviderSlotOccupied = "pack.install.activate.provider_slot_occupied";

    /// <summary>Activation refused: this pack shares a content key with another installed pack and the
    /// owning pack has NOT been resolved (no dependency chain, no recorded choice) — the F4 fail-closed
    /// gate. Resolve ownership (record a choice / declare a dependency), then activate.</summary>
    public const string ActivateUnresolvedCollision = "pack.install.activate.unresolved_collision";

    /// <summary>Activation refused because a persisted platform capability or version requirement is unmet.</summary>
    public const string ActivateUnmetPlatformRequirement =
        "pack.install.activate.unmet_platform_requirement";

    /// <summary>Activation refused because Access administration requires the released platform pack
    /// to be active first. This is an activation admission rule, not a hosting registration convention.</summary>
    public const string ActivatePlatformPackRequired =
        "pack.install.activate.platform_pack_required";

    /// <summary>Deactivation refused: the named installed version is not the pack's current Active version.</summary>
    public const string DeactivateNotActive = "pack.install.deactivate.not_active";

    /// <summary>An ACTIVATE was attempted with no acting principal — the pointer flip that makes seed
    /// content LIVE is at least as consequential as install, so the same domain-layer principal
    /// requirement applies (ticket 151 cluster; "any compiled caller bypasses").</summary>
    public const string ActivateRefusedNoPrincipal = "pack.install.activate.no_principal";

    /// <summary>A DEACTIVATE was attempted with no acting principal (same posture as
    /// <see cref="ActivateRefusedNoPrincipal"/> — the reverse pointer flip).</summary>
    public const string DeactivateRefusedNoPrincipal = "pack.install.deactivate.no_principal";

    /// <summary>An INSTALL was attempted with no acting principal in the <see cref="PackInstallContext"/>
    /// (ticket 151). The install pipeline commits a seed layer, so the domain layer itself requires the
    /// server-derived authenticated principal — a compiled caller cannot bypass the host route's
    /// <c>packages:operate</c> gate by invoking the installer directly with an anonymous context.
    /// Preview stays principal-free (it never mutates).</summary>
    public const string RefusedNoPrincipal = "pack.install.refused.no_principal";

    /// <summary>A narrowing was refused: the pack key has no Active version to narrow.</summary>
    public const string NarrowNotActive = "pack.install.narrow.not_active";

    /// <summary>A narrowing was refused: the Active version declares no such content key.</summary>
    public const string NarrowUnknownContentKey = "pack.install.narrow.unknown_content_key";

    /// <summary>A NARROW was attempted with no acting principal (same posture as
    /// <see cref="DeactivateRefusedNoPrincipal"/> — it changes what the tenant's live definitions say).</summary>
    public const string NarrowRefusedNoPrincipal = "pack.install.narrow.no_principal";

    /// <summary>A pack operation was refused before authorization because its pack key was blank.</summary>
    public const string RefusedBlankPackKey = "pack.install.refused.blank_pack_key";

    /// <summary>A pack operation was refused before authorization because its version was blank.</summary>
    public const string RefusedBlankVersion = "pack.install.refused.blank_version";

    /// <summary>The authorization gate denied a pack operation before any mutation.</summary>
    public const string RefusedAuthorizationDenied = "pack.install.refused.authorization_denied";
}

/// <summary>
/// The break-glass ceremony token (S-8). Its PRESENCE authorizes overriding an S-8 watermark refusal
/// (version downgrade / safety-floor weakening) — and ONLY those; it can NEVER override a verification,
/// revocation, or admission refusal (those are hard security refusals). A break-glass is always audited
/// as a distinct, loud <see cref="Audit.PackInstallAuditAction.BreakGlassOverride"/> entry.
/// </summary>
/// <param name="Justification">The operator's explicit reason (required — the ceremony, not a flag).</param>
/// <param name="AuthorizingPrincipal">The principal who authorized the override (server-derived from the
/// authenticated identity at the trust boundary — NOT a client-asserted value).</param>
public sealed record BreakGlass(string Justification, string AuthorizingPrincipal)
{
    /// <summary>The ceremony is meaningless without a real reason — an empty/whitespace justification is
    /// rejected at the DOMAIN layer, so no caller (route, test, or future adapter) can mint a blank one.</summary>
    public string Justification { get; } = string.IsNullOrWhiteSpace(Justification)
        ? throw new ArgumentException("Break-glass justification is required (the ceremony, not a flag).", nameof(Justification))
        : Justification;

    /// <summary>The authorizing principal must be a real, non-empty identity — a blank principal would
    /// leave the loud S-8 audit entry unattributable.</summary>
    public string AuthorizingPrincipal { get; } = string.IsNullOrWhiteSpace(AuthorizingPrincipal)
        ? throw new ArgumentException("Break-glass authorizing principal is required.", nameof(AuthorizingPrincipal))
        : AuthorizingPrincipal;
}

/// <summary>The kind of an S-8 watermark hit surfaced in the preview.</summary>
public enum PackWatermarkHitKind
{
    /// <summary>The version is below the installed watermark (downgrade).</summary>
    VersionDowngrade = 0,

    /// <summary>A safety floor is lowered below the watermark across versions.</summary>
    FloorWeakened = 1,
}

/// <summary>One S-8 watermark hit (a downgrade or a floor weakening) surfaced in the install-preview.</summary>
/// <param name="Kind">The hit kind.</param>
/// <param name="Detail">Human-readable detail (the version pair / the weakened floor keys).</param>
public sealed record PackWatermarkHit(PackWatermarkHitKind Kind, string Detail);

/// <summary>The plan-level verdict of a preview / install.</summary>
public enum PackInstallVerdict
{
    /// <summary>A clean first install would proceed.</summary>
    WouldInstall = 0,

    /// <summary>A clean upgrade would proceed (possibly with re-attach conflicts to review).</summary>
    WouldUpgrade = 1,

    /// <summary>Refused — see <see cref="PackInstallPreview.RefusalCodes"/> (hard; break-glass cannot override).</summary>
    Refused = 2,

    /// <summary>Refused ONLY on an S-8 watermark hit — a break-glass ceremony could override it.</summary>
    RequiresBreakGlass = 3,
}

/// <summary>The reason a declared platform requirement is unmet.</summary>
public enum PackPlatformRequirementFailure
{
    /// <summary>The running build does not provide the named capability.</summary>
    MissingCapability = 0,

    /// <summary>The capability exists, but the running platform is below its declared version floor.</summary>
    PlatformVersionFloor = 1,
}

/// <summary>A signed platform requirement the running build cannot satisfy.</summary>
/// <param name="Capability">The missing or version-gated capability.</param>
/// <param name="MinimumPlatformVersion">The optional declared platform-version floor.</param>
/// <param name="DeclaredBy">The content key or manifest pack key that declared the requirement.</param>
/// <param name="Failure">Why the requirement is unmet.</param>
public sealed record PackUnmetPlatformRequirement(
    string Capability,
    string? MinimumPlatformVersion,
    string DeclaredBy,
    PackPlatformRequirementFailure Failure);

/// <summary>A stable install-refusal code paired with the RFC 6901 location it describes.</summary>
public sealed record PackInstallRefusal(string Code, string Pointer);

/// <summary>
/// The install-preview — the D8 "moment of trust" surface (design §5). It answers WHO signed, WHAT would
/// change (new seeds, the upgrade version pair), the re-attach CONFLICT report, any S-8 watermark hits,
/// revocation staleness, and admission refusals — in a plain-language-ready structured form — WITHOUT any
/// mutation.
/// </summary>
/// <param name="Verdict">The plan-level verdict.</param>
/// <param name="PackKey">The pack key.</param>
/// <param name="Version">The pack version being previewed.</param>
/// <param name="SignerKeyId">WHO signed (base64url), when verified.</param>
/// <param name="Epoch">The signing epoch, when verified.</param>
/// <param name="VouchingScope">The trust scope that vouched (own-roster / channel), when verified.</param>
/// <param name="IsUpgrade">True if a prior version is installed.</param>
/// <param name="PriorVersion">The version being upgraded FROM, or null on a first install.</param>
/// <param name="NewSeedKeys">The content keys the new seed layer introduces / replaces.</param>
/// <param name="Conflicts">The re-attach conflict report (modified-both-sides / orphaned / floor-clamped).</param>
/// <param name="WatermarkHits">The S-8 downgrade / floor-weakening hits.</param>
/// <param name="AdmissionRefusals">The ADR 0143 admission refusals (S-9), when any.</param>
/// <param name="RevocationStale">Whether the revocation list is stale/absent (surfaced, not blocking).</param>
/// <param name="RefusalCodes">The stable refusal codes when <see cref="Verdict"/> is a refusal.</param>
/// <param name="CrossPackCollisions">The cross-pack same-key collisions this candidate would create with
/// already-installed packs (ADR 0129 D4/D5; the F4 fix). Each names BOTH pack ids + the shared key +
/// whether it resolves (dependency chain / recorded choice) or awaits an explicit owning-pack choice.
/// SURFACED, never silently first-wins-merged (S-2). Install stays additive; an UNRESOLVED collision is
/// enforced fail-closed at ACTIVATE, not here.</param>
/// <param name="UnmetContentReferences">The declared content-grain cross-app references (design note §6.3,
/// slice G2) whose target app / contributable is NOT installed. Non-empty ⇒ a fail-closed refusal
/// (<see cref="PackInstallCodes.RefusedUnmetContentReference"/>): the client renders "requires
/// &lt;toPackKey&gt; — install it first" from these structured entries + the refusal code, never an English
/// literal. Empty on a would-install/upgrade (every declared reference resolves).</param>
/// <param name="UnmetPlatformRequirements">Signed manifest or definition-envelope requirements the
/// running build cannot satisfy. Each entry names the capability and its declarer for client rendering.</param>
public sealed record PackInstallPreview(
    PackInstallVerdict Verdict,
    string PackKey,
    string Version,
    string? SignerKeyId,
    long? Epoch,
    TrustScope? VouchingScope,
    bool IsUpgrade,
    string? PriorVersion,
    IReadOnlyList<string> NewSeedKeys,
    IReadOnlyList<PackReattachConflict> Conflicts,
    IReadOnlyList<PackWatermarkHit> WatermarkHits,
    IReadOnlyList<PackAdmissionRefusal> AdmissionRefusals,
    bool RevocationStale,
    IReadOnlyList<string> RefusalCodes,
    IReadOnlyList<PackCrossPackCollision> CrossPackCollisions,
    IReadOnlyList<PackUnmetContentReference> UnmetContentReferences,
    IReadOnlyList<PackUnmetPlatformRequirement> UnmetPlatformRequirements)
{
    /// <summary>Constructs the pre-capability shape with no unmet platform requirements.</summary>
    public PackInstallPreview(
        PackInstallVerdict Verdict,
        string PackKey,
        string Version,
        string? SignerKeyId,
        long? Epoch,
        TrustScope? VouchingScope,
        bool IsUpgrade,
        string? PriorVersion,
        IReadOnlyList<string> NewSeedKeys,
        IReadOnlyList<PackReattachConflict> Conflicts,
        IReadOnlyList<PackWatermarkHit> WatermarkHits,
        IReadOnlyList<PackAdmissionRefusal> AdmissionRefusals,
        bool RevocationStale,
        IReadOnlyList<string> RefusalCodes,
        IReadOnlyList<PackCrossPackCollision> CrossPackCollisions,
        IReadOnlyList<PackUnmetContentReference> UnmetContentReferences)
        : this(
            Verdict,
            PackKey,
            Version,
            SignerKeyId,
            Epoch,
            VouchingScope,
            IsUpgrade,
            PriorVersion,
            NewSeedKeys,
            Conflicts,
            WatermarkHits,
            AdmissionRefusals,
            RevocationStale,
            RefusalCodes,
            CrossPackCollisions,
            UnmetContentReferences,
            Array.Empty<PackUnmetPlatformRequirement>())
    {
    }

    /// <summary>Manifest-declared pack dependencies (ADR 0129 D3 single-level slice) that do NOT resolve
    /// to an installed pack at the pinned-or-newer version (ticket 152). Non-empty ⇒ a HARD fail-closed
    /// refusal (<see cref="PackInstallCodes.RefusedUnmetDependency"/>) — the client renders "requires
    /// &lt;key&gt; v&lt;pin&gt; — install it first" from these structured entries, never an English
    /// literal. Empty on a would-install/upgrade.</summary>
    public IReadOnlyList<PackUnmetDependency> UnmetDependencies { get; init; }
        = Array.Empty<PackUnmetDependency>();

    /// <summary>Stable refusal codes paired with their RFC 6901 locations.</summary>
    public IReadOnlyList<PackInstallRefusal> Refusals { get; init; }
        = Array.Empty<PackInstallRefusal>();
}

/// <summary>
/// One manifest-declared pack dependency whose target is not installed (or is installed only below the
/// pinned version) — the ticket 152 fail-closed presence check. Stable tokens for client localization.
/// </summary>
/// <param name="DependencyKey">The declared dependency pack key.</param>
/// <param name="PinnedVersion">The version the manifest pins.</param>
/// <param name="InstalledVersion">The best installed version of the key, or null when none is installed.</param>
/// <param name="MalformedPin">True when <see cref="PinnedVersion"/> does not parse as a pinned version
/// (<see cref="PackVersion.IsWellFormed"/>) — refused with
/// <see cref="PackInstallCodes.RefusedMalformedDependencyPin"/> instead of being compared, because an
/// unparseable pin degrades to 0 and would be trivially satisfied (fail-open on a restrict).</param>
public sealed record PackUnmetDependency(
    string DependencyKey,
    string PinnedVersion,
    string? InstalledVersion,
    bool MalformedPin = false);

/// <summary>
/// One declared content-grain cross-app reference (design note §6.3, slice G2) whose target is NOT installed —
/// the "requires X — not installed" seam surfaced fail-closed in the install preview. All fields are stable
/// tokens the client localizes; there is no English display text (validation-codes doctrine).
/// </summary>
/// <param name="FromContentKey">The referencing contributable in the pack being installed.</param>
/// <param name="ToPackKey">The required (missing) app — the name the client renders in "requires X".</param>
/// <param name="ToContentKey">The specific required contributable in <see cref="ToPackKey"/>.</param>
/// <param name="Relation">The reference relation (a stable token; e.g. <c>ParentOf</c>).</param>
public sealed record PackUnmetContentReference(
    string FromContentKey,
    string ToPackKey,
    string ToContentKey,
    Graph.PackFeatureEdgeRelation Relation);

/// <summary>
/// The outcome of an <see cref="IPackInstaller.Install"/> — whether a seed layer was committed, the
/// action taken, the version, the refusal codes (on refusal), and the <see cref="Preview"/> plan (the
/// conflict report + watermark hits) that produced it.
/// </summary>
/// <param name="Installed">Whether an immutable seed layer was committed (Draft).</param>
/// <param name="Action">The audited action (Installed / Upgraded / Refused / BreakGlassOverride).</param>
/// <param name="PackKey">The pack key.</param>
/// <param name="Version">The version installed (or the refused version).</param>
/// <param name="RefusalCodes">Stable refusal codes when <see cref="Installed"/> is false.</param>
/// <param name="Preview">The plan (conflict report, watermark hits, admission, provenance).</param>
/// <param name="BrokeGlass">Whether a break-glass ceremony overrode an S-8 refusal.</param>
/// <param name="Decision">The packages-operation decision carried into synchronous projections.</param>
public sealed record PackInstallOutcome(
    bool Installed,
    Audit.PackInstallAuditAction Action,
    string PackKey,
    string Version,
    IReadOnlyList<string> RefusalCodes,
    PackInstallPreview Preview,
    bool BrokeGlass,
    AuthorizationDecision? Decision = null);

/// <summary>The outcome of an activation (ADR 0011 Draft/Inactive → Active pointer flip).</summary>
/// <param name="Activated">Whether the pointer flipped.</param>
/// <param name="PackKey">The pack key.</param>
/// <param name="Version">The version activated.</param>
/// <param name="Error">A stable error code when <see cref="Activated"/> is false (e.g. not installed,
/// a provider-slot occupied refusal, or an unresolved cross-pack collision).</param>
/// <param name="Detail">A human-readable detail for a refusal — e.g. the incumbent provider pack a
/// slot-occupied refusal names, or the contested key + the other pack an unresolved-collision refusal
/// names — so the surface is honest about WHY, not just a bare code. <c>null</c> on success.</param>
/// <param name="Projected">Whether synchronous projection completed.</param>
/// <param name="ProjectionResult">Opaque host-owned projection summary; never carries authority.</param>
/// <param name="Decision">The exact decision that admitted or refused the activation request.</param>
public sealed record PackActivationOutcome(
    bool Activated, string PackKey, string Version, string? Error, string? Detail = null,
    bool Projected = false,
    object? ProjectionResult = null,
    AuthorizationDecision? Decision = null);

/// <summary>The outcome of a reversible Active → Inactive pointer flip.</summary>
/// <param name="Deactivated">Whether the Active pointer was removed.</param>
/// <param name="PackKey">The pack key.</param>
/// <param name="Version">The version deactivated.</param>
/// <param name="Error">A stable error code when the named version was not Active; null on success.</param>
/// <param name="Projected">Whether synchronous reverse projection completed.</param>
/// <param name="ProjectionResult">Opaque host-owned projection summary; never carries authority.</param>
/// <param name="Decision">The exact decision that admitted or refused the deactivation request.</param>
public sealed record PackDeactivationOutcome(
    bool Deactivated, string PackKey, string Version, string? Error,
    bool Projected = false,
    object? ProjectionResult = null,
    AuthorizationDecision? Decision = null);

/// <summary>
/// The outcome of an administrator NARROWING one content key of a pack's Active version (ticket 208
/// L624). <see cref="Recorded"/> means the RFC-7396 overlay patch is persisted as the ordinary tenant
/// override; the next projection pass applies it, every later pass re-applies it, and the S-10 re-attach
/// carries it onto the next version's seed. <see cref="RefusalCode"/> is set exactly when it is not —
/// notably <see cref="Merge.PackTenantNarrowing.WideningRefusedCode"/> for an overlay that would widen.
/// </summary>
/// <param name="Recorded">Whether the override was persisted.</param>
/// <param name="PackKey">The pack key.</param>
/// <param name="ContentKey">The seed content key the overlay targets.</param>
/// <param name="RefusalCode">The stable refusal code, or null on success.</param>
/// <param name="WideningPath">The first JSON path that widened, when the refusal is a widening.</param>
/// <param name="Decision">The authorization decision the write was made under.</param>
public sealed record PackNarrowingOutcome(
    bool Recorded,
    string PackKey,
    string ContentKey,
    string? RefusalCode = null,
    string? WideningPath = null,
    AuthorizationDecision? Decision = null);

/// <summary>
/// The scope-vs-content policy (S-13). v1's two first-party roots (own-roster + Harborline channel) may
/// seed ANY content — this returns true for both. The method EXISTS as the fail-closed extension point:
/// enabling a future non-first-party (third-party) root requires teaching this policy to refuse CP floors
/// / effecting workflows for that scope, and until then no such scope can reach a <c>Verified</c> verdict
/// (there is no "install anyway"). So the check is a structural placeholder, not a v1 refusal path.
/// </summary>
public static class PackScopePolicy
{
    /// <summary>True iff <paramref name="scope"/> may seed the pack. Both v1 first-party scopes may.</summary>
    public static bool MaySeed(TrustScope scope)
        => scope is TrustScope.OwnRoster or TrustScope.HarborlineChannel;
}

/// <summary>
/// The context a single install/preview runs in: the tenant, the trust store (channel + own-roster), the
/// revocation list (S-11), the clock, the revocation staleness horizon, and an optional break-glass token.
/// </summary>
/// <param name="Tenant">The tenant the install is scoped to.</param>
/// <param name="TrustStore">The trust roots (own-roster + Harborline channel).</param>
/// <param name="Revocation">The revocation list checked at install (S-11).</param>
/// <param name="Now">The current instant (audit + staleness).</param>
/// <param name="RevocationMaxAge">The staleness horizon for the revocation list.</param>
/// <param name="BreakGlass">The break-glass ceremony token, or null (the common case).</param>
/// <param name="Principal">The ACTING principal — server-derived from the authenticated identity at the
/// trust boundary, never a client-asserted value (ticket 151). REQUIRED by
/// <see cref="IPackInstaller.Install"/> (an install with no principal is refused
/// <see cref="PackInstallCodes.RefusedNoPrincipal"/>); optional for <see cref="IPackInstaller.Preview"/>,
/// which never mutates.</param>
/// <param name="OwnershipResolutions">Optional content-key ownership choices applied only after authorization.</param>
public sealed record PackInstallContext(
    TenantId Tenant,
    IPackTrustStore TrustStore,
    IPackRevocationList Revocation,
    DateTimeOffset Now,
    TimeSpan RevocationMaxAge,
    BreakGlass? BreakGlass = null,
    string? Principal = null,
    IReadOnlyDictionary<string, string>? OwnershipResolutions = null);
