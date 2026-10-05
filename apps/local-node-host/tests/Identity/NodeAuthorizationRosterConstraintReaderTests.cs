using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// DES-0029 kernel-core-ck-11 — the roster facts the gate records are the ones the durable authority proves.
/// Mutation evidence: docs/evidence/ck11-roster-mutation-2026-09-29.md.
/// </summary>
public sealed class NodeAuthorizationRosterConstraintReaderTests
{
    private static readonly Guid Team = Guid.Parse("7e57cccc-0000-0000-0000-000000000c11");
    private static readonly Guid OtherTeam = Guid.Parse("7e57cccc-0000-0000-0000-0000000000ff");
    private static readonly TenantId Tenant = new(Team.ToString("D"));
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);
    private static readonly ActorId Founder = new("founder");

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task A_principal_with_no_team_registry_is_never_recorded_as_a_registry_member()
    {
        var inputs = await Reader(registry: null).ReadAsync(Founder, Tenant, Now);

        Assert.NotNull(inputs);
        Assert.False(inputs.RegistryMember);
    }

    [Theory]
    [Trait("Holds", "kernel-core-ck-11")]
    [InlineData(new[] { "this" }, true)]
    [InlineData(new[] { "other", "this" }, true)]
    [InlineData(new[] { "other" }, false)]
    [InlineData(new string[0], false)]
    public async Task Registry_membership_is_recorded_only_for_the_requested_team(string[] teams, bool expected)
    {
        var memberships = teams.Select(team => Membership(team == "this" ? Team : OtherTeam)).ToArray();

        var inputs = await Reader(new FixedRegistry(memberships)).ReadAsync(Founder, Tenant, Now);

        Assert.NotNull(inputs);
        Assert.Equal(expected, inputs.RegistryMember);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-11")]
    public async Task An_unreadable_roster_confers_no_membership_and_the_gate_reads_the_principal_as_ejected()
    {
        var reader = new NodeAuthorizationRosterConstraintReader(new NullPartyReader(), new RefusingRosterReader());

        Assert.Null(await reader.ReadAsync(Founder, Tenant, Now));

        var decision = await TestAuthorization.Gate(_ => true, reader).DecideAsync(
            new AuthorizationWriteContext(Founder, Tenant, AdmittedInstant.FromRecordedAct(Now))
                .Request(AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), "record", "ck11"));
        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.True(decision.Evidence.Roster!.Ejected);
        Assert.False(decision.Evidence.Roster.Member);
        Assert.False(decision.Evidence.Roster.RegistryMember);
    }

    private static NodeAuthorizationRosterConstraintReader Reader(ITeamRegistry? registry)
    {
        using var founder = KeyPair.Generate();
        var roster = MemberRoster.Genesis(
            Team, Founder.Value, new Ed25519Signer(founder), new Ed25519Verifier(), Now, Guid.NewGuid());
        return new NodeAuthorizationRosterConstraintReader(new NullPartyReader(), new FixedRosterReader(roster), registry);
    }

    private static TeamMembership Membership(Guid team) =>
        new(team, "Team", "Member", new KeyFingerprint("ck11-fingerprint"));

    private sealed class FixedRegistry(IReadOnlyList<TeamMembership> memberships) : ITeamRegistry
    {
        public ValueTask<IReadOnlyList<TeamMembership>> GetMembershipsAsync(ActorId actor, CancellationToken ct = default) =>
            ValueTask.FromResult(memberships);
    }

    private sealed class FixedRosterReader(MemberRoster roster) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) => Task.FromResult(roster);
    }

    private sealed class RefusingRosterReader : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) =>
            throw new VerifiedTenantRosterRefusedException(VerifiedTenantRosterRefusal.MissingGenesis, "no genesis");
    }

    private sealed class NullPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }
}
