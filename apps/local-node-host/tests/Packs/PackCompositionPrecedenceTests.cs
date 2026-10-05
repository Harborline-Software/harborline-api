using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-1014 (DES-0029 kernel-core-ck-3, T-724 ruling 124): the shipped content-key ownership precedence, pinned
/// on the one shared detector. A dependency chain decides first; otherwise a recorded owner choice decides,
/// only while it names a pack currently claiming the key; otherwise the collision is unresolved and activation
/// refuses it (<see cref="PackMultiPackCollisionRouteTests"/> pins the refusal code end to end). Oracles are
/// literal pack keys.
/// </summary>
public sealed class PackCompositionPrecedenceTests
{
    private const string Key = "shared.equipment";
    private static readonly IReadOnlyDictionary<string, string> NoChoice = new Dictionary<string, string>();

    [Fact(DisplayName = "T-1014 ruling 124 (1): the claimant depending on every other claimant owns the key")]
    public void Chain_resolves_the_owner()
    {
        var collision = Single([Claim("pack.a", "pack.b"), Claim("pack.b")], NoChoice);

        Assert.Equal(PackKeyOwnershipResolution.ResolvedByDependencyChain, collision.Resolution);
        Assert.Equal("pack.a", collision.OwnerPackKey);
    }

    [Fact(DisplayName = "T-1014 ruling 124 (2): with no chain, a choice naming a current claimant owns the key")]
    public void Choice_resolves_the_owner()
    {
        var collision = Single([Claim("pack.a"), Claim("pack.b")], Choice("pack.b"));

        Assert.Equal(PackKeyOwnershipResolution.ResolvedByChoice, collision.Resolution);
        Assert.Equal("pack.b", collision.OwnerPackKey);
    }

    [Fact(DisplayName = "T-1014 ruling 124: a chain owner beats a conflicting recorded choice")]
    public void Chain_beats_a_conflicting_choice()
    {
        var collision = Single([Claim("pack.a", "pack.b"), Claim("pack.b")], Choice("pack.b"));

        Assert.Equal(PackKeyOwnershipResolution.ResolvedByDependencyChain, collision.Resolution);
        Assert.Equal("pack.a", collision.OwnerPackKey);
    }

    [Fact(DisplayName = "T-1014 ruling 124: a choice naming a pack that no longer claims the key is ignored")]
    public void Stale_choice_is_ignored()
    {
        // pack.c is installed but its representative version does not ship the key.
        var claims = new[] { Claim("pack.a"), Claim("pack.b"), new PackKeyClaim("pack.c", [new("c.only", PackContentKind.AssetTypeDefinition)], []) };

        var collision = Single(claims, Choice("pack.c"));

        Assert.Equal(PackKeyOwnershipResolution.RequiresChoice, collision.Resolution);
        Assert.Null(collision.OwnerPackKey);
        Assert.Equal(["pack.a", "pack.b"], collision.ClaimingPackKeys);
    }

    [Theory(DisplayName = "T-1014 ruling 124 (3): a mutual or partial chain with no choice identifies no owner")]
    [InlineData("mutual")]
    [InlineData("partial")]
    public void Ambiguous_chain_without_choice_is_unresolved(string shape)
    {
        PackKeyClaim[] claims = shape == "mutual"
            ? [Claim("pack.a", "pack.b"), Claim("pack.b", "pack.a")]
            : [Claim("pack.a", "pack.b"), Claim("pack.b"), Claim("pack.c")];

        var collision = Single(claims, NoChoice);

        Assert.Equal(PackKeyOwnershipResolution.RequiresChoice, collision.Resolution);
        Assert.Null(collision.OwnerPackKey);
    }

    [Fact(DisplayName = "T-1014 ruling 124 (3): no chain and no choice identifies no owner, naming every claimant")]
    public void No_chain_and_no_choice_is_unresolved()
    {
        var collision = Single([Claim("pack.b"), Claim("pack.a")], NoChoice);

        Assert.Equal(PackKeyOwnershipResolution.RequiresChoice, collision.Resolution);
        Assert.Null(collision.OwnerPackKey);
        Assert.Equal(Key, collision.ContentKey);
        Assert.Equal(["pack.a", "pack.b"], collision.ClaimingPackKeys);
    }

    private static PackCrossPackCollision Single(IReadOnlyList<PackKeyClaim> claims, IReadOnlyDictionary<string, string> choices) =>
        Assert.Single(PackCompositionConflicts.Detect(claims, choices));

    private static PackKeyClaim Claim(string packKey, params string[] dependsOn) =>
        new(packKey, [new PackClaimedContent(Key, PackContentKind.AssetTypeDefinition)], dependsOn);

    private static Dictionary<string, string> Choice(string owner) => new() { [Key] = owner };
}
