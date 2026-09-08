using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// The desktop operator's seeded holdings survive an active-team switch (ticket 205 slice 3; slice 2
/// review-2 MINOR "the holding is seeded for the boot-active tenant only").
/// </summary>
/// <remarks>
/// Seeded grants are tenant-partitioned and every node route resolves its tenant from the CURRENTLY active
/// team (<see cref="NodeTenant.Resolve"/>), so a seed that ran only for the boot-active tenant left the
/// founder unable to unlock Build or touch a pack in every other bootstrap team. The composition under test
/// is the real one: the real <see cref="TeamContextFactory"/> and <see cref="ActiveTeamAccessor"/>, the real
/// <see cref="AccessGrantAuthorizationSeed"/> driven by the real
/// <see cref="AuthorizationSeedHostedService"/>, and the real <see cref="AuthorizationGate"/>.
/// </remarks>
public sealed class NodeOperatorHoldingAcrossTeamsTests
{
    private static readonly TeamId FirstTeam = new(new Guid("20500000-0000-0000-0000-0000000000a1"));
    private static readonly TeamId SecondTeam = new(new Guid("20500000-0000-0000-0000-0000000000a2"));
    private static readonly DateTimeOffset At = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The three operations the desktop founder must hold on whichever team is active.</summary>
    public static TheoryData<string> DesktopFounderOperations() => new()
    {
        Permission.WorkshopUnlock,
        Permission.PackagesOperate,
        Permission.PackagesAuthor,
        // Ticket 205 slice 4 — the record-scoped route families' install-wide acts (a list, a create, a
        // no-effect validate, the install's own authorization configuration).
        Permission.ContactsRead,
        Permission.ContactsCreate,
        Permission.SchedulingRead,
        Permission.SchedulingAuthor,
        Permission.SchedulingOperate,
        Permission.OrgManageSettings,
        TeamRolePermissions.RecordsRead,
        TeamRolePermissions.RecordsWrite,
        TeamRolePermissions.LedgerPost,
    };

    /// <summary>
    /// Ticket 205 slice 4 — the operations whose every route names the record it addresses. They are NOT
    /// declared install-wide, so the founder's holding has to be proved against a record target.
    /// </summary>
    public static TheoryData<string, string> DesktopFounderRecordScopedOperations() => new()
    {
        { Permission.ContactsWrite, "contacts" },
        { Permission.ContactsArchive, "contacts" },
        { Permission.FormsAuthor, "forms" },
        { Permission.SpatialRead, "spatial" },
    };

    [Theory]
    [MemberData(nameof(DesktopFounderRecordScopedOperations))]
    public async Task The_desktop_founder_holds_the_record_scoped_operation_on_both_teams_across_a_switch(
        string operation,
        string recordKind)
    {
        var (provider, factory, active) = await SeededTwoTeamInstallAsync();
        await using var _ = provider;
        var gate = provider.GetRequiredService<AuthorizationGate>();

        await AssertGrantedOnRecordAsync(gate, NodeTenant.Resolve(active), operation, recordKind);
        await active.SetActiveAsync(SecondTeam, CancellationToken.None);
        await AssertGrantedOnRecordAsync(gate, NodeTenant.Resolve(active), operation, recordKind);

        GC.KeepAlive(factory);
    }

    [Theory]
    [MemberData(nameof(DesktopFounderOperations))]
    public async Task The_desktop_founder_holds_the_operation_on_both_teams_across_a_switch(string operation)
    {
        var (provider, factory, active) = await SeededTwoTeamInstallAsync();
        await using var _ = provider;
        var gate = provider.GetRequiredService<AuthorizationGate>();

        // Boot-active team.
        Assert.Equal(FirstTeam, active.Active!.TeamId);
        await AssertGrantedAsync(gate, NodeTenant.Resolve(active), operation);

        // The operator switches teams. The routes' tenant follows, so the holding must too.
        await active.SetActiveAsync(SecondTeam, CancellationToken.None);
        Assert.Equal(SecondTeam, active.Active!.TeamId);
        await AssertGrantedAsync(gate, NodeTenant.Resolve(active), operation);

        GC.KeepAlive(factory);
    }

    [Fact]
    public async Task The_unlock_authority_answers_granted_on_the_switched_to_team()
    {
        var (provider, _, active) = await SeededTwoTeamInstallAsync();
        await using var __ = provider;

        // The whole point of the holding, read through the production authority rather than the raw gate.
        var authority = new NodeWorkshopUnlockAuthority(provider.GetRequiredService<AuthorizationGate>());
        await active.SetActiveAsync(SecondTeam, CancellationToken.None);

        var decision = await authority.AuthorizeAsync(new AuthorizationWriteContext(
            new ActorId(AccessGrantAuthorizationSeed.NodeOperatorPrincipal),
            NodeTenant.Resolve(active),
            At));

        Assert.IsType<WorkshopUnlockDecision.Granted>(decision);
    }

    [Fact]
    public async Task A_team_the_operator_never_materialised_is_not_seeded()
    {
        // The fence on the seeding rule: it covers the teams the node materialised, not every tenant id
        // that can be spelled. Without this a "seed everything" regression would read as a pass above.
        var (provider, _, _2) = await SeededTwoTeamInstallAsync();
        await using var __ = provider;
        var strangerTenant = ActiveTeamTenantContext.ProjectTenantId(
            new TeamId(new Guid("20500000-0000-0000-0000-0000000000ff")));

        var decision = await provider.GetRequiredService<AuthorizationGate>().DecideAsync(
            new AuthorizationWriteContext(
                new ActorId(AccessGrantAuthorizationSeed.NodeOperatorPrincipal), strangerTenant, At)
                .InstallWide(AuthorizationOperation.Parse(Permission.PackagesOperate)));

        Assert.NotEqual(AuthorizationVerdict.Allowed, decision.Verdict);
    }

    private static async Task AssertGrantedAsync(AuthorizationGate gate, TenantId tenant, string operation)
    {
        var decision = await gate.DecideAsync(
            new AuthorizationWriteContext(
                new ActorId(AccessGrantAuthorizationSeed.NodeOperatorPrincipal), tenant, At)
                .InstallWide(AuthorizationOperation.Parse(operation)));

        Assert.True(
            decision.Verdict == AuthorizationVerdict.Allowed,
            $"the desktop founder must hold {operation} on tenant {tenant.Value}; the gate said {decision.Verdict}");
    }

    private static async Task AssertGrantedOnRecordAsync(
        AuthorizationGate gate,
        TenantId tenant,
        string operation,
        string recordKind)
    {
        var decision = await gate.DecideAsync(
            new AuthorizationWriteContext(
                new ActorId(AccessGrantAuthorizationSeed.NodeOperatorPrincipal), tenant, At)
                .Request(AuthorizationOperation.Parse(operation), recordKind, "record-under-test"));

        Assert.True(
            decision.Verdict == AuthorizationVerdict.Allowed,
            $"the desktop founder must hold {operation} on a {recordKind} record in tenant {tenant.Value}; "
            + $"the gate said {decision.Verdict}");
    }

    /// <summary>Two materialised teams, seeded exactly the way the host seeds them at boot.</summary>
    private static async Task<(ServiceProvider Provider, ITeamContextFactory Factory, IActiveTeamAccessor Active)>
        SeededTwoTeamInstallAsync()
    {
        var factory = new TeamContextFactory(TimeProvider.System);
        await factory.GetOrCreateAsync(FirstTeam, "First Team", CancellationToken.None);
        await factory.GetOrCreateAsync(SecondTeam, "Second Team", CancellationToken.None);
        var active = new ActiveTeamAccessor(factory);
        await active.SetActiveAsync(FirstTeam, CancellationToken.None);

        var services = new ServiceCollection();
        services.AddAccessGrantModule();
        var provider = services.BuildServiceProvider();

        await new AuthorizationSeedHostedService(
            provider.GetRequiredService<AccessGrantAuthorizationSeed>(),
            active,
            factory,
            AuthorizationSeedProfile.Production,
            new FixedTimeProvider(At)).StartAsync(CancellationToken.None);

        return (provider, factory, active);
    }

    private sealed class FixedTimeProvider(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
