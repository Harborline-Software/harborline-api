using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.People;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class CanonicalPrincipalPartyReaderTests
{
    [Fact]
    [Trait("PlanCard", "ADM-02")]
    public async Task ResolveAsync_IsLosslessTenantExplicitFailClosedAndRestartDurable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-adm02-party-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using (var provider = await NewProviderAsync(databasePath))
            {
                await SeedAsync(provider);
                await AssertContractAsync(provider);
            }

            SqliteConnection.ClearAllPools();

            await using (var reopened = await NewProviderAsync(databasePath))
                await AssertContractAsync(reopened);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ServiceProvider> NewProviderAsync(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddNodeContacts();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return provider;
    }

    private static async Task SeedAsync(ServiceProvider provider)
    {
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        await SeedPartyBindingAsync(provider, tenantA, "party:not-a-guid:A", "same-user");
        await SeedPartyBindingAsync(
            provider,
            tenantB,
            "11111111-1111-1111-1111-111111111111",
            "same-user");
        await SeedPartyBindingAsync(provider, tenantA, "party:only-a", "only-a");
        await SeedPartyBindingAsync(provider, tenantA, "party:tombstoned", "deleted-user", tombstoned: true);
        await SeedPartyBindingAsync(provider, tenantA, "party:detached", "detached-user", detached: true);
        await SeedPartyBindingAsync(provider, tenantA, "party:ambiguous-a", "ambiguous-user");
        await SeedPartyBindingAsync(provider, tenantA, "party:ambiguous-b", "ambiguous-user");
    }

    private static async Task SeedPartyBindingAsync(
        ServiceProvider provider,
        TenantId tenant,
        string partyId,
        string principalUserId,
        bool tombstoned = false,
        bool detached = false)
    {
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        var actor = new PartyId("fixture-actor");
        var party = Party.Create(
            tenant,
            PartyKind.Person,
            partyId,
            actor,
            new Instant(System.TimeProvider.System.GetUtcNow()),
            new PartyId(partyId));
        if (tombstoned)
        {
            party = party with
            {
                DeletedAt = new Instant(System.TimeProvider.System.GetUtcNow()),
                DeletedBy = actor,
                DeletedReason = "fixture tombstone",
            };
        }

        var role = PartyRole.Create(
            tenant,
            party.Id,
            NodeEfPartyRepository.PrincipalUserBindingRoleName,
            principalUserId,
            actor,
            new Instant(System.TimeProvider.System.GetUtcNow()));
        if (detached)
            role = role.End(new Instant(System.TimeProvider.System.GetUtcNow()), "fixture detach", actor);

        await using var context = await factory.CreateDbContextAsync();
        context.Set<Party>().Add(party);
        context.Set<PartyRole>().Add(role);
        await context.SaveChangesAsync();
    }

    private static async Task AssertContractAsync(ServiceProvider provider)
    {
        var reader = provider.GetRequiredService<ICanonicalPrincipalPartyReader>();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var sameUser = new PrincipalUserId("same-user");

        var bindingA = await reader.ResolveAsync(tenantA, sameUser);
        var bindingB = await reader.ResolveAsync(tenantB, sameUser);

        Assert.NotNull(bindingA);
        Assert.Equal("party:not-a-guid:A", bindingA.PartyId.Value);
        Assert.Equal(tenantA, bindingA.VerifiedTenant);
        Assert.True(bindingA.IsPresent);
        Assert.False(bindingA.IsTombstoned);

        Assert.NotNull(bindingB);
        Assert.Equal("11111111-1111-1111-1111-111111111111", bindingB.PartyId.Value);
        Assert.Equal(tenantB, bindingB.VerifiedTenant);

        Assert.Null(await reader.ResolveAsync(tenantB, new PrincipalUserId("only-a")));
        Assert.Null(await reader.ResolveAsync(tenantA, new PrincipalUserId("missing-user")));
        Assert.Null(await reader.ResolveAsync(tenantA, new PrincipalUserId("deleted-user")));
        Assert.Null(await reader.ResolveAsync(tenantA, new PrincipalUserId("detached-user")));
        Assert.Null(await reader.ResolveAsync(tenantA, new PrincipalUserId("ambiguous-user")));
    }
}
