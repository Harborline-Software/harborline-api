using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Data.Compose;

/// <summary>
/// An in-memory SNAPSHOT of a pack the author is composing (Pack Composer B-2a, council Q3:
/// <b>SNAPSHOT-AT-COMPOSE; read-live REFUSED</b> — TOCTOU review-bypass, "you sign what you inspected").
/// At compose time the ceremony reads the selected authored artifacts from their live stores ONCE,
/// canonicalizes each into a frozen <see cref="ComposedLeaf"/>, and computes a <see cref="SnapshotHash"/>
/// over the whole frozen set. The human PII review (S-12 / FU-1) inspects THESE frozen leaf bytes; the
/// affirmation is bound to <see cref="SnapshotHash"/> SERVER-SIDE (S-1) — never a UI checkbox. Export
/// re-hashes the frozen set and refuses to sign if it no longer matches the affirmed hash.
/// </summary>
/// <remarks>
/// The snapshot is immutable once composed. Re-composing the same <see cref="ComposeId"/> REPLACES the
/// draft with a new frozen set + a new hash, which leaves any prior <see cref="Affirmation"/> pointing at
/// the OLD hash — so export then refuses (the human must re-review the changed content). The store is
/// per-node, in-memory (F5 durable store deferred, cerebrum 2026-07-06): a draft is ephemeral, cleared on
/// node restart — which is correct for an authoring scratchpad.
/// </remarks>
public sealed record DraftComposition
{
    /// <summary>The opaque compose-session id (a GUID string) the ceremony routes key on.</summary>
    public required string ComposeId { get; init; }

    /// <summary>The tenant this draft belongs to (resolved from the active team; isolation fence).</summary>
    public required TenantId Tenant { get; init; }

    /// <summary>The pack key being composed (S-10 upgrade-stable).</summary>
    public required string Key { get; init; }

    /// <summary>The pack version being composed.</summary>
    public required string Version { get; init; }

    /// <summary>The pack display name.</summary>
    public required string Name { get; init; }

    /// <summary>The pack description.</summary>
    public required string Description { get; init; }

    /// <summary>The pack scope tier.</summary>
    public required PackScopeTier ScopeTier { get; init; }

    /// <summary>The frozen, canonicalized content leaves (the exact bytes the human reviews + we sign).</summary>
    public required IReadOnlyList<ComposedLeaf> Leaves { get; init; }

    /// <summary>The Domain Compliance Profile (ADR 0145) declared for this pack. <c>null</c> is the
    /// "no DCP" state — the export gate refuses it (<c>pack.dcp.missing</c>); the compose route defaults it
    /// to the <c>general</c> grandfather so the UI never reaches that state.</summary>
    public required DomainComplianceProfile? Dcp { get; init; }

    /// <summary>Declared single-level dependencies (ADR 0129 D3).</summary>
    public required IReadOnlyList<PackDependencyRef> Dependencies { get; init; }

    /// <summary>Declared capability requirements.</summary>
    public required IReadOnlyList<string> CapabilityRequirements { get; init; }

    /// <summary>The content-address (SHA-256 CID string) of the whole frozen draft — the manifest digest the
    /// human review + affirmation bind to (S-1). Recomputable from the frozen fields alone.</summary>
    public required string SnapshotHash { get; init; }

    /// <summary>The bound human PII-review affirmation, or <c>null</c> until the author affirms. Export
    /// refuses (fail-closed) when this is <c>null</c> OR when <see cref="ComposeAffirmation.AffirmedHash"/>
    /// no longer equals <see cref="SnapshotHash"/>.</summary>
    public ComposeAffirmation? Affirmation { get; init; }

    /// <summary>Advisory compose-time findings surfaced to the author (#141) — non-fatal warnings that a
    /// projected artifact lost something the pinned content shape cannot carry (e.g. a type's form binding).
    /// Computed at compose from the LIVE descriptors (NOT recoverable from the frozen leaves, whose content
    /// no longer carries the dropped field), so it is stored on the draft and re-read on GET. It does NOT
    /// enter <see cref="SnapshotHash"/>: a warning annotates the snapshot, it is not part of the signed
    /// content. Empty when the composition is lossless (the common path).</summary>
    public IReadOnlyList<ComposeWarning> Warnings { get; init; } = Array.Empty<ComposeWarning>();
}

/// <summary>One frozen, content-addressed leaf of a <see cref="DraftComposition"/>.</summary>
/// <param name="Key">The stable content key within the pack.</param>
/// <param name="Kind">The declarative content kind.</param>
/// <param name="Version">The pinned content version.</param>
/// <param name="Content">The frozen canonical JSON body (read once at compose; never re-read live).</param>
/// <param name="ContentAddress">The SHA-256 CID (string) of the canonical bytes — the type's canonical
/// address (used for the per-item content-address-match acceptance).</param>
public sealed record ComposedLeaf(
    string Key,
    PackContentKind Kind,
    string Version,
    JsonNode Content,
    string ContentAddress);

/// <summary>The bound human PII-review affirmation (S-1 / S-12). Records the EXACT snapshot hash the human
/// reviewed and when — bound server-side so a UI cannot fake a checkbox against different bytes.</summary>
/// <param name="AffirmedHash">The <see cref="DraftComposition.SnapshotHash"/> the human reviewed + affirmed.</param>
/// <param name="AffirmedAtUtc">When the affirmation was recorded (node clock).</param>
public sealed record ComposeAffirmation(string AffirmedHash, DateTimeOffset AffirmedAtUtc);
