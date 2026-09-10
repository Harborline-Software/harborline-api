using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>Ticket 293 slice 4: the gate-owned install-root projection remains equivalent to the legacy closure read.</summary>
public sealed class RosterGrantEquivalenceTests
{
    private static readonly TenantId Tenant = new("29300000-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(4);

    [Fact]
    public async Task Install_root_projection_equals_legacy_closure_for_root_narrow_lapsed_revoked_and_absent_grants()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await using var provider = CreateProvider(store);
        await DesktopGrantSourceTests.SeedAsync(provider, Tenant, At, TeamRolePermissions.RecordsRead);
        var grants = provider.GetRequiredService<IGrantStore>();
        var role = AccessGrantAuthorizationSeed.NodeOperatorRole;

        var narrow = await AppendAsync(grants, "narrow", "/records/a", At.AddMinutes(-1), At.AddMinutes(1));
        _ = await AppendAsync(grants, "lapsed", "/", At.AddHours(-2), At.AddHours(-1));
        var revoked = await AppendAsync(grants, "revoked", "/", At.AddMinutes(-1), At.AddMinutes(1));
        await grants.RevokeAsync(Tenant, revoked.GrantId, new GrantRevocation(new ActorId("admin"), At,
            new GrantReason(GrantReasonCodes.RevocationReview)));

        var gate = provider.GetRequiredService<AuthorizationGate>();
        var closure = provider.GetRequiredService<IAuthorizationClosureReader>();
        foreach (var (shape, principal) in new[]
                 {
                     ("root", new ActorId("local")), ("narrow", new ActorId("narrow")),
                     ("lapsed", new ActorId("lapsed")), ("revoked", new ActorId("revoked")),
                     ("absent", new ActorId("absent")),
                 })
        {
            var actual = await gate.InstallRootPermissionsAsync(principal, Tenant, At);
            var expected = await LegacyInstallRootPermissionsAsync(closure, principal);
            Assert.Equal(expected, actual);
        }

        Assert.NotNull(narrow);
    }

    [Fact]
    public async Task Equivalent_roster_facts_with_identical_grants_produce_the_same_gate_verdict()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await using var provider = CreateProvider(store);
        await DesktopGrantSourceTests.SeedAsync(provider, Tenant, At, TeamRolePermissions.RecordsRead);
        var grants = provider.GetRequiredService<IGrantStore>();
        _ = await AppendAsync(grants, "replicated", "/", At.AddMinutes(-1), At.AddMinutes(1));
        var gate = provider.GetRequiredService<AuthorizationGate>();

        var local = await DecideAsync(gate, new ActorId("local"), new AuthorizationRosterInputs("local", true, false));
        var replicated = await DecideAsync(gate, new ActorId("replicated"), new AuthorizationRosterInputs("replicated", true, false));

        Assert.Equal(local.Verdict, replicated.Verdict);
        Assert.Equal(AuthorizationVerdict.Allowed, local.Verdict);
    }

    private static ServiceProvider CreateProvider(SearchTestStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestKernelClock();
        services.AddSingleton(store.Factory);
        AuthorizationAdminRouteTests.RegisterGate(services);
        return services.BuildServiceProvider();
    }

    private static Task<AccessGrant> AppendAsync(
        IGrantStore grants, string principal, string scope, DateTimeOffset validFrom, DateTimeOffset validUntil) =>
        grants.AppendAsync(Tenant, new AccessGrant(GrantId.New(), Tenant, new ActorId(principal),
            AccessGrantAuthorizationSeed.NodeOperatorRole, ScopeExpression.Parse(scope), GrantResidency.Cache,
            new GrantValidity(validFrom, validUntil), GranterKind.Person, new ActorId("admin"), validFrom,
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId("admin")),
            validFrom), principal + "-fixture");

    private static async ValueTask<PermissionSet> LegacyInstallRootPermissionsAsync(
        IAuthorizationClosureReader closure, ActorId principal)
    {
        var atoms = await closure.UserPermissionsAsync(Tenant, principal, At);
        return PermissionSet.From(atoms.Atoms.Where(atom => atom.Scope.Value == "/").Select(atom => atom.Operation.Value));
    }

    private static ValueTask<AuthorizationDecision> DecideAsync(
        AuthorizationGate gate, ActorId principal, AuthorizationRosterInputs roster) =>
        gate.DecideAsync(new AuthorizationWriteContext(principal, Tenant, At)
            .Request(AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead), "record", "fixture") with
            { Roster = roster with { RequireMember = true, RequireGrantCoverage = true } });
}
