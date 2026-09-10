using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Boot registry labels and permission sets cannot authorize a desktop caller.</summary>
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
        // Ticket 293 slice 4 — the operator's authority is what the gate derives from its GRANTS, and this
        // composition has none: a registry label alone must still answer nothing. An all-allowing gate would
        // be the one that holds a grant, which is not what "registry-only" means.
        var sut = new ActiveTeamAuthorizationContext(accessor, registry, TimeProvider.System,
            gate: Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Gate(false));
        return (accessor, sut);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Registry_only_roles_never_supply_permission_verdicts(bool admin, bool readRoles)
    {
        var (accessor, sut) = await BuildAsync();
        accessor.Set(Materialize(admin ? AdminOrg : ViewerOrg));
        using var capture = new Harborline.Api.LocalNodeHost.Tests.Authorization.RosterDecisionCapture();
        if (readRoles) Assert.Empty(sut.Roles);
        else Assert.False(sut.HasPermission(TeamRolePermissions.RecordsRead));
        var evidence = capture.AssertSingle(false);
        Assert.True(evidence.Roster!.RegistryMember);
        Assert.False(evidence.Roster.Member);
        Assert.Contains("registry:member:True", evidence.Project()[1].Facts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("records")]
    [InlineData("Records:read")]
    [InlineData(" records:read")]
    [InlineData("records:read@/")]
    public async Task Noncanonical_permission_returns_false(string permission)
    {
        var (accessor, sut) = await BuildAsync();
        accessor.Set(Materialize(AdminOrg));
        Assert.False(sut.HasPermission(permission));
    }

    [Fact]
    public async Task Roles_enumeration_appends_no_refusal_but_an_act_does()
    {
        var (accessor, _) = await BuildAsync();
        accessor.Set(Materialize(AdminOrg));
        using var key = Harborline.Api.Foundation.Crypto.KeyPair.Generate();
        var trail = new Harborline.Api.Kernel.Audit.InMemoryAuditTrail();
        var audit = new Harborline.Api.LocalNodeHost.Health.AuthorizationRefusalAudit(trail,
            new Harborline.Api.Foundation.Crypto.Ed25519Signer(key),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Harborline.Api.LocalNodeHost.Health.AuthorizationRefusalAudit>.Instance);
        var sut = new ActiveTeamAuthorizationContext(accessor, new InMemoryTeamRegistry(), TimeProvider.System,
            gate: Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Gate(false), refusalAudit: audit);
        var query = new Harborline.Api.Kernel.Audit.AuditQuery(ActiveTeamTenantContext.ProjectTenantId(AdminOrg));
        Assert.Empty(sut.Roles);
        Assert.Empty(sut.Roles);
        var rows = new System.Collections.Generic.List<Harborline.Api.Kernel.Audit.AuditRecord>();
        await foreach (var row in trail.QueryAsync(query)) rows.Add(row);
        Assert.Empty(rows);
        Assert.False(sut.HasPermission(TeamRolePermissions.RecordsRead));
        await foreach (var row in trail.QueryAsync(query)) rows.Add(row);
        Assert.Single(rows);
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
