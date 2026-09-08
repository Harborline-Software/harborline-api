using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// How a cross-pack same-key collision RESOLVES to an owning pack (ADR 0129 D4/D5). A collision is
/// two DISTINCT installed packs that ship the SAME content key — the F4 gap the B-1f slice closes
/// (previously the projector silently first-wins-skipped the loser at
/// <c>PackSeedProjector</c>). It is <b>never silent</b>: it either resolves by a declared
/// dependency chain, resolves by an explicit recorded client choice, or awaits one.
/// </summary>
public enum PackKeyOwnershipResolution
{
    /// <summary>No dependency chain orders the claimants and no client choice is recorded yet — the
    /// collision is SURFACED and awaits an explicit owning-pack choice (D5 same-tier rule: the
    /// composition must name the winner, else it is a composition error — here, a fail-closed
    /// refuse-to-activate). This is the fail-closed default.</summary>
    RequiresChoice = 0,

    /// <summary>Exactly one claimant declares a dependency (ADR 0129 D3 <c>Dependencies[]</c>) on every
    /// other claimant, so it composes OVER them and wins by D5 specificity — resolved WITHOUT ceremony
    /// (the intentional-layering path; no invented <c>dependsOn</c>/<c>extends</c> field, the existing
    /// dependency edge is reused).</summary>
    ResolvedByDependencyChain = 1,

    /// <summary>An explicit client choice naming the owning pack for this key was recorded and persisted
    /// (D8 per-key ownership) — the same-tier collision the operator resolved deliberately.</summary>
    ResolvedByChoice = 2,
}

/// <summary>
/// One cross-pack same-key collision: a content key that two or more DISTINCT installed packs both
/// declare. Carries BOTH pack ids and the key (so the D8 install-preview / provenance surface can name
/// them), the resolution status, and — when resolved — the owning pack.
/// </summary>
/// <param name="ContentKey">The contested content key (the seed / content key both packs ship).</param>
/// <param name="ContentKind">The declarative kind of the contested content.</param>
/// <param name="ClaimingPackKeys">Every distinct pack key that ships <see cref="ContentKey"/> (sorted,
/// ordinal). Length ≥ 2.</param>
/// <param name="Resolution">Whether/how the collision resolves to an owner.</param>
/// <param name="OwnerPackKey">The owning pack key when <see cref="Resolution"/> is a resolved value;
/// <c>null</c> when it <see cref="PackKeyOwnershipResolution.RequiresChoice"/>.</param>
public sealed record PackCrossPackCollision(
    string ContentKey,
    PackContentKind ContentKind,
    IReadOnlyList<string> ClaimingPackKeys,
    PackKeyOwnershipResolution Resolution,
    string? OwnerPackKey);

/// <summary>One content key a pack declares — the collision-detection unit.</summary>
/// <param name="ContentKey">The stable content key.</param>
/// <param name="Kind">The declarative kind.</param>
public sealed record PackClaimedContent(string ContentKey, PackContentKind Kind);

/// <summary>
/// What one pack claims for cross-pack collision detection: its key, the content keys it ships, and the
/// keys of the packs it declares a dependency on (ADR 0129 D3 <c>Dependencies[]</c> — reused, not a new
/// field). One entry per DISTINCT pack key.
/// </summary>
/// <param name="PackKey">The pack key.</param>
/// <param name="Contents">The content keys this pack ships.</param>
/// <param name="DependencyKeys">The keys of the packs this pack declares a dependency on (0129 D3).</param>
public sealed record PackKeyClaim(
    string PackKey,
    IReadOnlyList<PackClaimedContent> Contents,
    IReadOnlyList<string> DependencyKeys);

/// <summary>
/// The shared, pure cross-pack collision detector — the single source consumed by BOTH the
/// install-preview (does this candidate collide with an already-installed pack?) and the seed projector
/// (which owner do I project for a contested key, and which contested keys are unresolved?). Keeping ONE
/// detector avoids the A4 two-implementations-drift class of defect between what the preview surfaces and
/// what the projector enforces.
/// </summary>
public static class PackCompositionConflicts
{
    /// <summary>
    /// Detects every content key claimed by two or more DISTINCT packs in <paramref name="claims"/> and
    /// resolves each to an owner: by a declared dependency chain (0129 D3/D5 precedence, no ceremony),
    /// else by a recorded client choice in <paramref name="recordedChoices"/> (contentKey → owning pack
    /// key), else it <see cref="PackKeyOwnershipResolution.RequiresChoice"/> (fail-closed). A key shipped
    /// by a single pack is NOT contested and is omitted. Deterministic (ordinal-sorted output).
    /// </summary>
    public static IReadOnlyList<PackCrossPackCollision> Detect(
        IReadOnlyList<PackKeyClaim> claims,
        IReadOnlyDictionary<string, string> recordedChoices)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(recordedChoices);

        // contentKey → (kind, distinct-pack-keys-that-ship-it).
        var byKey = new Dictionary<string, (PackContentKind Kind, SortedSet<string> Packs)>(StringComparer.Ordinal);
        // packKey → the set of pack keys it declares a dependency on (0129 D3).
        var depsByPack = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var claim in claims)
        {
            depsByPack[claim.PackKey] = new HashSet<string>(claim.DependencyKeys, StringComparer.Ordinal);
            foreach (var content in claim.Contents)
            {
                if (!byKey.TryGetValue(content.ContentKey, out var entry))
                {
                    entry = (content.Kind, new SortedSet<string>(StringComparer.Ordinal));
                    byKey[content.ContentKey] = entry;
                }

                entry.Packs.Add(claim.PackKey);
            }
        }

        var collisions = new List<PackCrossPackCollision>();
        foreach (var (contentKey, entry) in byKey.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (entry.Packs.Count < 2)
            {
                continue; // shipped by a single pack — not a cross-pack collision.
            }

            var claimants = entry.Packs.ToList();
            var (resolution, owner) = Resolve(contentKey, claimants, depsByPack, recordedChoices);
            collisions.Add(new PackCrossPackCollision(contentKey, entry.Kind, claimants, resolution, owner));
        }

        return collisions;
    }

    /// <summary>
    /// Projects durable install state into collision CLAIMS — one <see cref="PackKeyClaim"/> per DISTINCT
    /// installed pack key (its representative version's content keys + declared dependency edges). A
    /// same-key clash between two VERSIONS of one pack key is an upgrade (S-10 re-attach), not a cross-pack
    /// collision, so a pack key collapses to a single representative: its Active version if any, else its
    /// highest installed version. <paramref name="excludePackKey"/> drops one key entirely (the preview
    /// excludes the candidate's own prior versions). The SINGLE mapping both the installer's activation
    /// guard and the node's seed projector use — so "what packs claim what keys" is defined once.
    /// </summary>
    public static List<PackKeyClaim> ClaimsFromInstalled(
        IEnumerable<InstalledPack> installed, string? excludePackKey = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return installed
            .Where(p => excludePackKey is null || !string.Equals(p.PackKey, excludePackKey, StringComparison.Ordinal))
            .GroupBy(p => p.PackKey, StringComparer.Ordinal)
            .Select(RepresentativeVersion)
            .Select(p => new PackKeyClaim(
                p.PackKey,
                p.SeedItems.Select(s => new PackClaimedContent(s.Key, s.Kind)).ToList(),
                p.Dependencies.Select(d => d.Key).ToList()))
            .ToList();
    }

    private static InstalledPack RepresentativeVersion(IEnumerable<InstalledPack> versions)
    {
        var list = versions as IReadOnlyList<InstalledPack> ?? versions.ToList();
        return list.FirstOrDefault(p => p.Lifecycle == PackLifecycleState.Active)
            ?? list.OrderBy(p => p.Version, Comparer<string>.Create(PackVersion.Compare)).Last();
    }

    private static (PackKeyOwnershipResolution Resolution, string? Owner) Resolve(
        string contentKey,
        IReadOnlyList<string> claimants,
        IReadOnlyDictionary<string, HashSet<string>> depsByPack,
        IReadOnlyDictionary<string, string> recordedChoices)
    {
        // (1) Declared dependency chain (0129 D3/D5): the claimant that composes OVER every other claimant
        //     (declares a dependency on all of them) is the most-specific and wins WITHOUT ceremony. Only
        //     an UNAMBIGUOUS single such claimant resolves the chain — a mutual/partial chain is ambiguous
        //     and falls through to explicit choice (fail-closed; v1 keeps the single-level dep constraint).
        var chainOwners = claimants
            .Where(c => depsByPack.TryGetValue(c, out var deps)
                && claimants.Where(o => !string.Equals(o, c, StringComparison.Ordinal)).All(deps.Contains))
            .ToList();
        if (chainOwners.Count == 1)
        {
            return (PackKeyOwnershipResolution.ResolvedByDependencyChain, chainOwners[0]);
        }

        // (2) Explicit recorded client choice (D8 per-key ownership) — only honored if it names an actual
        //     claimant of THIS key (a stale choice for a pack that no longer ships the key does not resolve).
        if (recordedChoices.TryGetValue(contentKey, out var chosen)
            && claimants.Contains(chosen, StringComparer.Ordinal))
        {
            return (PackKeyOwnershipResolution.ResolvedByChoice, chosen);
        }

        // (3) Fail-closed: surfaced, awaiting an explicit owning-pack choice.
        return (PackKeyOwnershipResolution.RequiresChoice, null);
    }
}
