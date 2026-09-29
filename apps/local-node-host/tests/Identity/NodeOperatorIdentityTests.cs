using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// DES-0029 kernel-core-ck-11 — a composition that lacks either half of the operator binding (the roster or the
/// node's signing key) has no desktop actor; it answers null rather than faulting on the missing half.
/// Mutation evidence: docs/evidence/ck11-roster-mutation-2026-09-29.md.
/// </summary>
public sealed class NodeOperatorIdentityTests
{
    private static readonly Guid Team = Guid.Parse("7e57cccc-0000-0000-0000-00000000c110");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public void A_roster_without_a_node_signer_has_no_operator()
    {
        using var founder = KeyPair.Generate();
        var roster = MemberRoster.Genesis(Team, "founder", new Ed25519Signer(founder), new Ed25519Verifier(), Now, Guid.NewGuid());

        Assert.Null(new NodeOperatorIdentity(new NodeTeamRoster(roster), signer: null).Principal);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public void A_node_signer_without_a_roster_has_no_operator()
    {
        using var node = KeyPair.Generate();

        Assert.Null(new NodeOperatorIdentity(roster: null, new Ed25519Signer(node)).Principal);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public void The_operator_is_the_party_bound_to_the_node_key()
    {
        using var founder = KeyPair.Generate();
        var signer = new Ed25519Signer(founder);
        var roster = MemberRoster.Genesis(Team, "founder", signer, new Ed25519Verifier(), Now, Guid.NewGuid());

        Assert.Equal("founder", new NodeOperatorIdentity(new NodeTeamRoster(roster), signer).Principal?.Value);
    }
}
