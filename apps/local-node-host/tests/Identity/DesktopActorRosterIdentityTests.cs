using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Ticket 294 slice 3 — the ejection refusal is keyed by the same identity on every plane. The gate never
/// consults caller-supplied roster facts; it derives them through the PRODUCTION
/// <see cref="NodeAuthorizationRosterConstraintReader"/>. The desktop plane asks the gate about its actor, which
/// must be the roster party this node's signing key is bound to (slice 3a mapped the old constant onto it; slice 3b
/// made the actor that party), or an ejected founder keeps every grant held under the desktop actor.
/// </summary>
public sealed class DesktopActorRosterIdentityTests
{
    private static readonly Guid Team = Guid.Parse("7e57cccc-0000-0000-0000-000000000294");
    private static readonly TenantId Tenant = new(Team.ToString("D"));
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);
    private const string NodeParty = "principal-node-294";

    // Ticket 294 slice 3b: the desktop actor is what the desktop plane presents, the party bound to the node key.
    private static ActorId DesktopActor(MemberRoster roster, IOperationSigner node) =>
        new NodeOperatorIdentity(new Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster(roster), node).Principal!.Value;

    [Theory]
    [Trait("PlanCard", "294-s3")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_ejected_node_party_is_refused_when_the_desktop_actor_asks_the_production_gate(
        bool presentDesktopActor)
    {
        using var founder = KeyPair.Generate();
        using var node = KeyPair.Generate();
        var verifier = new Ed25519Verifier();
        var founderSigner = new Ed25519Signer(founder);
        var roster = MemberRoster.Genesis(Team, "founder", founderSigner, verifier, Now, Guid.NewGuid())
            .Admit("founder", founderSigner, NodeParty, node.PrincipalId,
                PermissionCompositions.Admin, verifier, Now, Guid.NewGuid())
            .Revoke("founder", NodeParty);

        var nodeSigner = new Ed25519Signer(node);
        var decision = await DecideAsync(roster, nodeSigner,
            presentDesktopActor ? DesktopActor(roster, nodeSigner) : new ActorId(NodeParty));

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.True(decision.Evidence.Roster!.Ejected);
    }

    [Fact]
    [Trait("PlanCard", "294-s3")]
    public async Task The_desktop_actor_reads_the_live_roster_edge_bound_to_this_nodes_key()
    {
        using var founder = KeyPair.Generate();
        using var node = KeyPair.Generate();
        var verifier = new Ed25519Verifier();
        var founderSigner = new Ed25519Signer(founder);
        var roster = MemberRoster.Genesis(Team, "founder", founderSigner, verifier, Now, Guid.NewGuid())
            .Admit("founder", founderSigner, NodeParty, node.PrincipalId,
                PermissionCompositions.Admin, verifier, Now, Guid.NewGuid());

        var nodeSigner = new Ed25519Signer(node);
        var decision = await DecideAsync(roster, nodeSigner, DesktopActor(roster, nodeSigner));

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.True(decision.Evidence.Roster!.Member);
        Assert.Equal(NodeParty, decision.Evidence.Roster.PartyId);
    }

    private static async Task<AuthorizationDecision> DecideAsync(
        MemberRoster roster, IOperationSigner nodeSigner, ActorId principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICanonicalPrincipalPartyReader>(new NullPartyReader());
        services.AddSingleton<IVerifiedTenantRosterReader>(new FixedRosterReader(roster));
        services.AddSingleton(nodeSigner);
        // The production registration (NodeRosterComposition.AddNodeRoster) of the gate's roster reader.
        services.AddSingleton<IAuthorizationRosterConstraintReader, NodeAuthorizationRosterConstraintReader>();
        await using var provider = services.BuildServiceProvider();

        // Every grant stands in for the seeded desktop-operator holdings: only the roster can refuse.
        var gate = TestAuthorization.Gate(_ => true,
            provider.GetRequiredService<IAuthorizationRosterConstraintReader>());
        return await gate.DecideAsync(new AuthorizationWriteContext(principal, Tenant, AdmittedInstant.FromRecordedAct(Now))
            .Request(AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), "record", "desktop"));
    }

    private sealed class FixedRosterReader(MemberRoster roster) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) => Task.FromResult(roster);
    }

    private sealed class NullPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }
}
