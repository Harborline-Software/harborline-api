using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Shared test helper: registers a fixed-active-team <see cref="IActiveTeamAccessor"/> for node route
/// tests whose DI graph composes the financial write services. Since ADR 0032's identity layer bound the
/// node's ambient <c>ITenantContext</c> to the active team (<c>ActiveTeamTenantContext</c>), any test that
/// calls <c>AddNodeFinancialPosting</c> / <c>AddNodeBillWrites</c> / <c>AddNodeInvoiceWrites</c> /
/// <c>AddNodePaymentWrites</c> must supply an <see cref="IActiveTeamAccessor"/> with a materialized team
/// for the tenant context to resolve (the interface-expansion-blast-radius fix — patch the fakes).
/// </summary>
public static class NodeTestActiveTeam
{
    /// <summary>The fixed team id these tests activate (its id projects to the ambient data tenant).</summary>
    public static readonly TeamId TestTeamId =
        new(Guid.Parse("7e570000-0000-0000-0000-0000000000aa"));

    /// <summary>A ready-made active-team accessor with a fixed materialized team, for passing directly
    /// to a route's <c>Map(..., IActiveTeamAccessor)</c> in tests that build their own host.</summary>
    public static IActiveTeamAccessor Accessor { get; } =
        new FakeActiveTeamAccessor(
            new TeamContext(TestTeamId, "Test Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

    /// <summary>Register a fixed materialized active team on <paramref name="services"/>.</summary>
    public static IServiceCollection AddTestActiveTeam(this IServiceCollection services)
    {
        var team = new TeamContext(TestTeamId, "Test Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        services.AddSingleton<IActiveTeamAccessor>(new FakeActiveTeamAccessor(team));
        return services;
    }

    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public FakeActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
