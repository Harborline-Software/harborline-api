using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Calendar;

/// <summary>
/// Calendar-productization #149 slice C1 — the genesis-layer default-calendar provisioner
/// (<see cref="DefaultCalendarProvisioningService"/>). Proves: a fresh founder tenant auto-provisions
/// exactly one default "My calendar"; the provision is idempotent (a restart adds no second default);
/// a tenant that already has calendars is untouched; and no active team is a calm no-op.
/// </summary>
public sealed class DefaultCalendarProvisioningServiceTests
{
    private static readonly TeamId Team = new(Guid.Parse("cccc0000-0000-0000-0000-0000000000c1"));
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(Team);

    [Fact(DisplayName = "provisioner: a fresh tenant gets exactly one default 'My calendar'")]
    public async Task Provisions_OneDefault()
    {
        var store = new InMemoryCalendarStore();
        var svc = NewService(store, active: TeamContextFor(Team));

        await svc.StartAsync(CancellationToken.None);

        var calendars = await store.ListAsync(Tenant);
        var cal = Assert.Single(calendars);
        Assert.True(cal.IsDefault);
        Assert.Equal(CalendarKind.Personal, cal.Kind);
        Assert.Equal(OwnedCalendar.DefaultNameKey, cal.Name); // the i18n key, not a hardcoded literal
        Assert.Equal(DefaultCalendarProvisioningService.FounderActorId, cal.OwnerActorId);
    }

    [Fact(DisplayName = "provisioner: is idempotent — a re-run adds no second default")]
    public async Task Idempotent()
    {
        var store = new InMemoryCalendarStore();
        var svc = NewService(store, active: TeamContextFor(Team));

        await svc.StartAsync(CancellationToken.None);
        await svc.StartAsync(CancellationToken.None); // a node restart

        Assert.Single(await store.ListAsync(Tenant));
    }

    [Fact(DisplayName = "provisioner: a tenant that already has a calendar is untouched")]
    public async Task DoesNotProvision_WhenCalendarsExist()
    {
        var store = new InMemoryCalendarStore();
        // Pre-existing non-default calendar (e.g. a user already created one).
        await store.SaveAsync(OwnedCalendar.Create(Tenant, "Team", CalendarKind.Team, Guid.NewGuid()));
        var svc = NewService(store, active: TeamContextFor(Team));

        await svc.StartAsync(CancellationToken.None);

        var calendars = await store.ListAsync(Tenant);
        Assert.Single(calendars);
        Assert.False(calendars[0].IsDefault); // no default was force-added over the existing calendar
    }

    [Fact(DisplayName = "provisioner: no active team → calm no-op (does not throw)")]
    public async Task NoActiveTeam_NoOp()
    {
        var store = new InMemoryCalendarStore();
        var svc = NewService(store, active: null);

        await svc.StartAsync(CancellationToken.None); // must not throw

        Assert.Empty(await store.ListAsync(Tenant));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static DefaultCalendarProvisioningService NewService(ICalendarStore store, TeamContext? active)
        => new(store, new MutableActiveTeamAccessor(active),
            NullLogger<DefaultCalendarProvisioningService>.Instance);

    private static TeamContext TeamContextFor(TeamId teamId)
        => new(teamId, "Founder Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
