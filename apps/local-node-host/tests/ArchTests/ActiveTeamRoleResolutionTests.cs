using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ADR 0032 identity layer — per-org role resolution. Proves <see cref="ActiveTeamAuthorizationContext"/>
/// resolves the OS-user's role from the membership edge for the ACTIVE org and answers
/// <c>HasPermission</c> accordingly (survey #1275 §3): the role is per-(person, active org), so the same
/// operator yields a different permission set when the active org changes.
/// </summary>
public sealed class ActiveTeamRoleResolutionTests
{
    private static readonly TeamId AdminOrg = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    private static readonly TeamId ViewerOrg = new(Guid.Parse("b0000000-0000-0000-0000-00000000000b"));

    private static TeamContext Materialize(TeamId id) =>
        new(id, "Org", new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            TimeProvider.System);

    private static TeamMembership Membership(TeamId id, TeamRole role) =>
        new(id.Value, "Org", TeamRolePermissions.DisplayName(role),
            Harborline.Api.Foundation.Crypto.KeyFingerprint.FromPublicKey(id.Value.ToByteArray()), role);

    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active { get; private set; }
        public void Set(TeamContext? t) => Active = t;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private static async Task<(FakeActiveTeamAccessor accessor, ActiveTeamAuthorizationContext sut)> BuildAsync()
    {
        var registry = new InMemoryTeamRegistry();
        var op = ActiveTeamAuthorizationContext.NodeOperator;
        await registry.AddMembershipAsync(op, Membership(AdminOrg, TeamRole.Admin));
        await registry.AddMembershipAsync(op, Membership(ViewerOrg, TeamRole.Viewer));

        var accessor = new FakeActiveTeamAccessor();
        var sut = new ActiveTeamAuthorizationContext(accessor, registry, TimeProvider.System);
        return (accessor, sut);
    }

    [Fact(DisplayName = "ADR0032: HasPermission resolves the ACTIVE org's role (Admin org => full perms)")]
    public async Task HasPermission_InAdminOrg_GrantsLedgerPost()
    {
        var (accessor, sut) = await BuildAsync();
        accessor.Set(Materialize(AdminOrg));

        Assert.True(sut.HasPermission(TeamRolePermissions.LedgerPost));
        Assert.True(sut.HasPermission(TeamRolePermissions.MembersManage));
        Assert.True(sut.HasPermission(TeamRolePermissions.RecordsWrite));
        Assert.Contains("Admin", sut.Roles);
    }

    [Fact(DisplayName = "ADR0032: switching the active org SWITCHES the resolved role/permissions")]
    public async Task SwitchingActiveOrg_SwitchesResolvedRole()
    {
        var (accessor, sut) = await BuildAsync();

        accessor.Set(Materialize(AdminOrg));
        Assert.True(sut.HasPermission(TeamRolePermissions.LedgerPost));

        // Same operator, different active org — now a Viewer.
        accessor.Set(Materialize(ViewerOrg));
        Assert.False(sut.HasPermission(TeamRolePermissions.LedgerPost));
        Assert.False(sut.HasPermission(TeamRolePermissions.RecordsWrite));
        Assert.True(sut.HasPermission(TeamRolePermissions.RecordsRead));
        Assert.Contains("Viewer", sut.Roles);
    }

    [Fact(DisplayName = "ADR0032: no active team => no role, no permissions")]
    public async Task NoActiveTeam_NoPermissions()
    {
        var (_, sut) = await BuildAsync();
        // accessor left with null active team.
        Assert.False(sut.HasPermission(TeamRolePermissions.RecordsRead));
        Assert.Empty(sut.Roles);
    }

    [Fact(DisplayName = "ADR0032: operator not a member of the active org => no permissions")]
    public async Task NonMemberOfActiveOrg_NoPermissions()
    {
        var (accessor, sut) = await BuildAsync();
        var strangerOrg = new TeamId(Guid.Parse("c0000000-0000-0000-0000-00000000000c"));
        accessor.Set(Materialize(strangerOrg));

        Assert.False(sut.HasPermission(TeamRolePermissions.RecordsRead));
        Assert.Empty(sut.Roles);
    }

    [Fact(DisplayName = "ADR0032: UserId is the single-office OS-user (the install-constant LocalUserId)")]
    public async Task UserId_IsTheOsOperator()
    {
        var (_, sut) = await BuildAsync();
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, sut.UserId);
    }
}
