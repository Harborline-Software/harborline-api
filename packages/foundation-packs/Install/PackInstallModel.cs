using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// The lifecycle state of an installed pack version (ADR 0011 Draft/Inactive → Active). Install creates the
/// immutable seed layer in <see cref="Draft"/>; activation flips it to <see cref="Active"/>; a prior
/// Active version becomes <see cref="Superseded"/> when a newer one activates — but its immutable seed
/// layer is NEVER deleted (deactivation = pointer flip, S-2), so a rollback within the ADR 0011 window
/// is a pointer flip, not a re-install.
/// </summary>
public enum PackLifecycleState
{
    /// <summary>The seed layer exists but is not the resolving version (freshly installed, not yet activated).</summary>
    Draft = 0,

    /// <summary>The version the cascade resolves for this pack key (exactly one Active per key per tenant).</summary>
    Active = 1,

    /// <summary>A prior version replaced by a newer Active one — its immutable seed layer is retained.</summary>
    Superseded = 2,

    /// <summary>
    /// A previously Active version whose resolving pointer was deliberately removed. Its immutable seed layer,
    /// tenant overrides, ownership choices, and watermark remain intact so re-activation is a reversible pointer
    /// flip; no captured tenant data is deleted.
    /// </summary>
    Inactive = 3,
}

/// <summary>
/// One entry of an installed pack's IMMUTABLE seed layer (ADR 0101 Rev 3.1 F2). The canonical bytes are
/// the verified content (their <see cref="ContentAddress"/> was merkle-checked at verify) — they are a
/// shared template, never a tenant-editable row; a tenant edit is a separate <see cref="PackTenantOverride"/>.
/// </summary>
/// <param name="Key">The stable content key within the pack (the re-attach key, S-10).</param>
/// <param name="Kind">The declarative kind.</param>
/// <param name="Version">The pinned item version.</param>
/// <param name="CanonicalJson">The item's canonical JSON (UTF-8 text form of the verified bytes).</param>
/// <param name="ContentAddress">The verified SHA-256 content address of the canonical bytes.</param>
public sealed record PackSeedItem(
    string Key,
    PackContentKind Kind,
    string Version,
    string CanonicalJson,
    Cid ContentAddress)
{
    /// <summary>Parses <see cref="CanonicalJson"/> to a fresh <see cref="JsonNode"/> (never shared/mutated).</summary>
    public JsonNode ParseContent()
        => JsonNode.Parse(CanonicalJson)
           ?? throw new InvalidOperationException($"Seed item '{Key}' canonical JSON parsed to null.");
}

/// <summary>
/// A tenant-authored override layered on an installed pack's seed item (ADR 0101 F2 — the tenant-scoped
/// override row atop the immutable seed). The <see cref="OverlayPatch"/> is an RFC-7396 JSON-Merge-Patch
/// authored against the seed version the tenant edited; upgrade re-attaches it via the three-way merge.
/// </summary>
/// <param name="ContentKey">The seed content key this override targets (S-10 re-attach key).</param>
/// <param name="OverlayPatch">The RFC-7396 patch (the tenant's edit relative to the seed it was authored on).</param>
public sealed record PackTenantOverride(string ContentKey, JsonNode OverlayPatch)
{
    /// <summary>Deep-copies the overlay patch (JsonNode is mutable; never share a live tree across records).</summary>
    public PackTenantOverride DeepCopy() => new(ContentKey, OverlayPatch.DeepClone());
}

/// <summary>
/// A single installed pack version — its immutable seed layer (<see cref="SeedItems"/>), lifecycle, the
/// safety-floor set it declared, and the provenance of the artifact it was installed from (signer +
/// epoch + vouching scope). Immutable once committed.
/// </summary>
/// <param name="PackKey">The pack key (ADR 0129 D1).</param>
/// <param name="Version">The pinned pack version (immutable once installed, ADR 0011).</param>
/// <param name="ScopeTier">The pack scope-tier.</param>
/// <param name="Lifecycle">Draft / Active / Superseded / Inactive (ADR 0011).</param>
/// <param name="SeedItems">The immutable seed layer (F2).</param>
/// <param name="SafetyFloors">The safety floors this version's catalogs declared (floor key → strictness;
/// higher = stricter). The S-8 watermark is tracked separately and monotonically.</param>
/// <param name="InstalledAtUtc">When the seed layer was committed.</param>
/// <param name="SignerKeyId">The signer whose signature this pack verified against (provenance).</param>
/// <param name="Epoch">The signing epoch (ADR 0126 D4).</param>
/// <param name="VouchingScope">The trust scope that vouched (own-roster / channel).</param>
/// <param name="Dependencies">The pack's declared single-level dependencies (ADR 0129 D3), persisted
/// from the signed manifest so cross-pack collision resolution can honor a declared dependency chain
/// (D5 precedence) without re-reading the pack file. Empty ⇒ a leaf pack.</param>
/// <param name="ProviderSlot">The exclusive category slot this pack fills when it is a category-provider
/// (ADR 0129 D4), persisted from the signed manifest so activation can enforce one-active-provider-per
/// -category. <c>null</c> ⇒ a domain-block / capability-plugin (no exclusivity).</param>
/// <param name="ContentReferences">The pack's declared content-grain cross-app reference edges (app-layer
/// design note §6.3, slice G2), persisted from the signed manifest so the feature graph's class-3 edge is
/// read off durable install state — never a body re-parse. <c>null</c>/empty ⇒ a pre-G2 / dependency-free
/// pack; the graph falls back to G1 body-parse for such a pack (backward-compatible).</param>
/// <param name="CapabilityRequirements">The signed manifest's platform capability requirements, persisted
/// so activation can re-evaluate them against the running build. <c>null</c>/empty means none.</param>
public sealed record InstalledPack(
    string PackKey,
    string Version,
    PackScopeTier ScopeTier,
    PackLifecycleState Lifecycle,
    IReadOnlyList<PackSeedItem> SeedItems,
    IReadOnlyDictionary<string, int> SafetyFloors,
    DateTimeOffset InstalledAtUtc,
    PrincipalId SignerKeyId,
    long Epoch,
    TrustScope VouchingScope,
    IReadOnlyList<PackDependencyRef> Dependencies,
    string? ProviderSlot = null,
    IReadOnlyList<PackContentReferenceEdge>? ContentReferences = null,
    IReadOnlyList<string>? CapabilityRequirements = null);

/// <summary>
/// The S-8 monotonic watermark persisted per installed pack key — the highest version ever installed and
/// the elementwise-max safety-floor set ever seen across versions. Install refuses (by default) a version
/// below <see cref="Version"/> (downgrade) OR an upgrade whose seed lowers any floor below
/// <see cref="Floors"/> (floor weakening ACROSS versions) — both a break-glass ceremony, never a silent
/// conflict click-through. The watermark only ever moves UP; it is NOT reduced by uninstall/rollback.
/// </summary>
/// <param name="PackKey">The pack key the watermark tracks.</param>
/// <param name="Version">The highest version ever installed for this key.</param>
/// <param name="Floors">The elementwise-max safety-floor set ever seen across all installed versions.</param>
public sealed record PackInstallWatermark(
    string PackKey,
    string Version,
    IReadOnlyDictionary<string, int> Floors);

/// <summary>
/// Extraction + monotonic-max helpers for the S-4/S-8 safety-floor axis. A pack content item declares
/// safety floors as a <c>"safetyFloors"</c> object of <c>{ floorKey: strictness }</c> (higher strictness =
/// stricter, mirroring the <c>ScoringCriticality</c> ordinal in blocks-assets-registry). The install
/// engine treats content as opaque JSON, so this is the decoupled convention through which a catalog's
/// declared floors participate in the cross-version watermark + the raise-strictness clamp.
/// </summary>
public static class PackSafetyFloors
{
    /// <summary>The reserved content property carrying a content item's declared safety floors.</summary>
    public const string FloorsProperty = "safetyFloors";

    /// <summary>
    /// The SINGLE-SOURCE floor extractor over a set of canonicalized content items — the union
    /// (elementwise-<see cref="Max"/>) of the <c>safetyFloors</c> declared by every <c>StandardsCatalog</c>
    /// item. This is what the INSTALL engine's S-8 watermark computes AND what the author-side floor guard
    /// (<c>PackAuthorFloorGuard</c>, §7.1) reuses, so author-time guidance and install-time enforcement can
    /// never diverge (council A-3 — no reimplementation, no A4-drift). Non-catalog kinds carry no floors.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ExtractFloors(IReadOnlyList<PackContentItem> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        IReadOnlyDictionary<string, int> floors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in contents)
        {
            if (item.Kind != PackContentKind.StandardsCatalog)
            {
                continue;
            }

            var node = JsonNode.Parse(Encoding.UTF8.GetString(item.CanonicalBytes.Span));
            floors = Max(floors, Extract(node));
        }

        return floors;
    }

    /// <summary>
    /// Extracts the declared safety floors from a content item's JSON — the <c>safetyFloors</c> object's
    /// integer-valued members. Absent / non-object / non-integer members are ignored (empty ⇒ no floors).
    /// </summary>
    public static IReadOnlyDictionary<string, int> Extract(JsonNode? content)
    {
        var floors = new Dictionary<string, int>(StringComparer.Ordinal);
        if (content is JsonObject obj
            && obj.TryGetPropertyValue(FloorsProperty, out var node)
            && node is JsonObject floorObj)
        {
            foreach (var (key, value) in floorObj)
            {
                if (value is JsonValue v && v.TryGetValue(out int strictness))
                {
                    floors[key] = strictness;
                }
            }
        }

        return floors;
    }

    /// <summary>Elementwise max of two floor sets — the monotonic watermark accumulation (raise-only).</summary>
    public static IReadOnlyDictionary<string, int> Max(
        IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        var result = new Dictionary<string, int>(a, StringComparer.Ordinal);
        foreach (var (key, value) in b)
        {
            result[key] = result.TryGetValue(key, out var existing) ? System.Math.Max(existing, value) : value;
        }

        return result;
    }

    /// <summary>
    /// The S-4 anti-laundering-by-OMISSION clamp: returns <paramref name="declared"/> with every floor it
    /// OMITTED filled in from the established <paramref name="watermark"/>. Under the ratified raise-only
    /// doctrine, an upgrade whose seed simply does NOT re-declare a floor must never let that floor vanish
    /// from the installed seed layer — a laundering-by-omission path equivalent to explicitly weakening it.
    /// A floor the seed DOES carry wins (so a raise lands, and an explicit — break-glass-authorized —
    /// weakening still lands with its lowered value); only an ABSENT key inherits the watermarked max.
    /// (<see cref="Max"/> would be wrong here: it would also raise an explicitly-lowered break-glass floor
    /// back up, silently negating the ceremony.)
    /// </summary>
    public static IReadOnlyDictionary<string, int> PreserveOmitted(
        IReadOnlyDictionary<string, int> declared, IReadOnlyDictionary<string, int> watermark)
    {
        // Start from the established watermark, then let every DECLARED floor overwrite — so a declared
        // value (raised or break-glass-lowered) wins, and any floor the declaration omitted persists.
        var result = new Dictionary<string, int>(watermark, StringComparer.Ordinal);
        foreach (var (key, value) in declared)
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// The floor keys in <paramref name="candidate"/> that fall BELOW the <paramref name="watermark"/> —
    /// i.e. the floors an upgrade would WEAKEN across versions (S-8 hard-refusal set). A floor absent from
    /// the candidate is NOT a weakening (the seed simply does not re-declare it; the watermark stands).
    /// </summary>
    public static IReadOnlyList<string> Weakened(
        IReadOnlyDictionary<string, int> watermark, IReadOnlyDictionary<string, int> candidate)
    {
        var weakened = new List<string>();
        foreach (var (key, candidateStrictness) in candidate)
        {
            if (watermark.TryGetValue(key, out var floor) && candidateStrictness < floor)
            {
                weakened.Add(key);
            }
        }

        weakened.Sort(StringComparer.Ordinal);
        return weakened;
    }
}
