using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class UnattributedGrantCompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("29400000-0000-4000-8000-000000000001");
    private const string Admin = "principal-admin";
    private const string Target = "principal-target";
    private const string Handle = "ticket294-admin-session";
    private static readonly GrantId TargetGrant = new(Guid.Parse("29400000-0000-4000-8000-000000000002"));

    private static readonly GrantId OtherGrant = new(Guid.Parse("29400000-0000-4000-8000-000000000003"));

    [Theory]
    [InlineData("missing")]
    [InlineData("tombstoned")]
    [InlineData("detached")]
    [InlineData("duplicated")]
    [InlineData("wrong-tenant")]
    [InlineData("resolved")]
    public async Task Composed_List_And_Revoke_Survive_Restart_And_The_Real_Gate_Refuses(string binding)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket294-{Guid.NewGuid():N}");
        try
        {
            await using (var host = await OpenAsync(directory))
            {
                await SeedAsync(host.Services, binding);
                await AssertListedAsync(host.Services, binding);
                Assert.Equal(AuthorizationVerdict.Allowed, await VerdictAsync(host.Services));
            }
            // Rebuild the actual Program service graph over the same durable stores, before and after revocation.
            await using (var restarted = await OpenAsync(directory))
            {
                await AssertListedAsync(restarted.Services, binding);
                if (binding == "resolved") return; // The resolving list retains its existing row contract.
                var result = await restarted.Services.GetRequiredService<IAdminTeamAccessAuthority>()
                    .RevokeMemberGrantAsync(Handle, Tenant.Value, TargetGrant.ToString(),
                        new AuthorizationWriteContext(new ActorId(Admin), Tenant, Now));
                Assert.Equal(AdminRevokeMemberStatus.Revoked, result?.Status);
                Assert.Equal(GrantStatus.Revoked, (await restarted.Services.GetRequiredService<IGrantStore>()
                    .FindAsync(Tenant, TargetGrant))!.Status);
                Assert.Equal(AuthorizationVerdict.Denied, await VerdictAsync(restarted.Services));
            }
            await using (var restarted = await OpenAsync(directory))
            {
                Assert.Equal(AuthorizationVerdict.Denied, await VerdictAsync(restarted.Services));
                var members = await restarted.Services.GetRequiredService<IAdminTeamAccessAuthority>()
                    .ListMembersAsync(Handle, Tenant.Value);
                Assert.NotNull(members);
                Assert.DoesNotContain(members.Members, member => member.GrantId == TargetGrant.ToString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertListedAsync(IServiceProvider services, string binding)
    {
        var result = await services.GetRequiredService<IAdminTeamAccessAuthority>().ListMembersAsync(Handle, Tenant.Value);
        Assert.NotNull(result);
        var row = Assert.Single(result.Members, member => member.GrantId == TargetGrant.ToString());
        Assert.Contains(TeamRolePermissions.MembersManage, row.Capabilities);
        if (binding == "resolved")
        {
            // Ticket 294 slice 2a — a grant-anchored row is surfaced under the ONE key (the canonical
            // tenant principal). The live People binding still decides attributed vs UNATTRIBUTED.
            Assert.Equal("principal-target", row.PartyId);
            Assert.Equal(TeamMemberSource.Grant, row.Source);
            Assert.Null(row.AttributionFailure);
        }
        else
        {
            var other = Assert.Single(result.Members, member => member.GrantId == OtherGrant.ToString());
            Assert.Equal("UNATTRIBUTED", other.PartyId);
            Assert.DoesNotContain(TeamRolePermissions.MembersManage, other.Capabilities);
            Assert.Equal("UNATTRIBUTED", row.PartyId);
            Assert.Equal(TeamMemberSource.Unattributed, row.Source);
            Assert.Equal("No unique live party binding in this tenant: missing, tombstoned, detached, duplicated or wrong-tenant.",
                row.AttributionFailure);
        }
        var admin = Assert.Single(result.Members, member => member.PartyId == "party-admin");
        Assert.Equal(TeamMemberSource.Roster, admin.Source);
        Assert.Null(admin.GrantId);
        Assert.Null(admin.AttributionFailure);
    }

    private static async Task<AuthorizationVerdict> VerdictAsync(IServiceProvider services) =>
        (await services.GetRequiredService<AuthorizationGate>().DecideAsync(
            new AuthorizationWriteContext(new ActorId(Target), Tenant, Now)
                .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", TargetGrant.ToString())))
        .Verdict;

    internal static async Task SeedAsync(IServiceProvider services, string binding)
    {
        await services.GetRequiredService<AccessGrantAuthorizationSeed>().InstallAsync(Tenant, Now, AuthorizationSeedProfile.Production);
        await SeedPartyAsync(services, Tenant, "party-admin", Admin, "resolved");
        if (binding != "missing")
            await SeedPartyAsync(services, binding == "wrong-tenant" ? new TenantId("other-tenant") : Tenant,
                "party-target", Target, binding);
        if (binding == "duplicated")
            await SeedPartyAsync(services, Tenant, "party-target-duplicate", Target, "resolved");
        var grants = services.GetRequiredService<IGrantStore>();
        var adminGrant = GrantId.New();
        foreach (var (id, principal) in new[] { (adminGrant, Admin), (TargetGrant, Target) })
            await grants.AppendAsync(Tenant, new AccessGrant(id, Tenant, new ActorId(principal),
                RoleReference.Administrator, ScopeExpression.Parse("/"), GrantResidency.Cache,
                new GrantValidity(Now.AddMinutes(-1)), GranterKind.Person, new ActorId(Admin), Now.AddMinutes(-1),
                new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId(Admin)), Now));
        if (binding != "resolved")
            await grants.AppendAsync(Tenant, new AccessGrant(OtherGrant, Tenant, new ActorId(Target),
                AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/"), GrantResidency.Cache,
                new GrantValidity(Now.AddMinutes(-1)), GranterKind.Person, new ActorId(Admin), Now.AddMinutes(-1),
                new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId(Admin)), Now));
        await using var identity = await services.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>()
            .CreateDbContextAsync();
        await identity.Database.MigrateAsync();
        identity.Accounts.Add(new InstallationAccountRecord
        {
            AccountId = "account-admin", NormalizedUsername = "ADMIN", CredentialHash = "digest",
            CredentialAlgorithm = "test", CredentialCeremonyId = "test", CredentialVersion = 1,
            Status = InstallationAccountStatus.Active, SecurityVersion = 1, OwnerVersion = 1,
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        });
        await identity.SaveChangesAsync();
        await using var search = await services.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>()
            .CreateDbContextAsync();
        var epoch = await search.GrantAuthorizationEpochs.SingleAsync(row => row.TenantId == Tenant.Value && row.PrincipalId == Admin);
        var version = await grants.FindVersionedAsync(Tenant, adminGrant);
        await using var sessions = await services.GetRequiredService<IDbContextFactory<NodeLocalWebSessionDbContext>>()
            .CreateDbContextAsync();
        await sessions.Database.MigrateAsync();
        sessions.UserSessions.Add(new WebUserSessionRecord("admin-session", "account-admin", 1, Tenant.Value,
            "admin-membership", 1, Admin, "party-admin", [new PinnedGrantOwnerVersion(adminGrant.ToString(), version!.OwnerVersion)],
            epoch.AuthorizationEpoch, AccountSetupInvitationStore.Digest(Handle), "antiforgery", "selection",
            Now.AddMinutes(-1), Now.AddHours(1), Now.AddHours(2), 1));
        await sessions.SaveChangesAsync();
    }

    private static async Task SeedPartyAsync(IServiceProvider services, TenantId tenant, string id, string principal, string binding)
    {
        var actor = new PartyId("fixture");
        var at = new Instant(Now);
        var party = Party.Create(tenant, PartyKind.Person, id, actor, at, new PartyId(id));
        if (binding == "tombstoned")
            party = party with { DeletedAt = at, DeletedBy = actor, DeletedReason = "test" };
        var role = PartyRole.Create(tenant, party.Id, NodeEfPartyRepository.PrincipalUserBindingRoleName, principal, actor, at);
        if (binding == "detached") role = role.End(at, "test", actor);
        await using var context = await services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>().CreateDbContextAsync();
        context.Set<Party>().Add(party);
        context.Set<PartyRole>().Add(role);
        await context.SaveChangesAsync();
    }

    internal static async Task<Host> OpenAsync(string directory)
    {
        IServiceProvider? provider = null;
        using var key = KeyPair.Generate();
        var roster = MemberRoster.Genesis(Guid.Parse(Tenant.Value), "party-admin", new Ed25519Signer(key),
            new Ed25519Verifier(), Now, Guid.NewGuid());
        await Assert.ThrowsAsync<ProbeComplete>(() => global::LocalNodeHostComposition.RunAsync(
            ["--environment=Production", "--LocalNode:RootSeedHex=" + new string('4', 64),
                "--LocalNode:WebClient:Enabled=true", "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                "--Logging:EventLog:LogLevel:Default=None"],
            sessionTokenOverride: "ticket294-probe", dataDirectory: directory, kernelClock: new Clock(),
            installFootprintRootOverride: directory, finalServiceProviderProbe: (services, factory) =>
            {
                // Only the signed roster scenario is supplied; all authorities, binding readers, stores,
                // authorization closure, gate and audit writers come from Program's shipping composition.
                services.AddSingleton<IVerifiedTenantRosterReader>(new RosterReader(roster));
                provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                throw new ProbeComplete();
            }));
        var host = new Host(provider!);
        try
        {
            await provider!.GetServices<IHostedService>().OfType<LocalNodeStoreEncryptionGuard>().Single()
                .StartAsync(CancellationToken.None);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private sealed class ProbeComplete : Exception;
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RosterReader(MemberRoster roster) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId tenant, CancellationToken ct) => Task.FromResult(roster);
    }
    internal sealed class Host(IServiceProvider services) : IAsyncDisposable
    {
        public IServiceProvider Services => services;
        public async ValueTask DisposeAsync()
        {
            if (services is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (services is IDisposable disposable) disposable.Dispose();
        }
    }
}
