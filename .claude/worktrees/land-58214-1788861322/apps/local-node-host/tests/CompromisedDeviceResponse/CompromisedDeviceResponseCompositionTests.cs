using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

public sealed class CompromisedDeviceResponseCompositionTests
{
    [Fact]
    public async Task AddNodeRoster_registers_the_live_response_service()
    {
        var verifier = new Ed25519Verifier();
        var signer = new Ed25519Signer(KeyPair.Generate());
        var teamId = Guid.Parse("71560000-0000-0000-0000-000000000002");
        var roster = MemberRoster.Genesis(
            teamId, "operator-a", signer, verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        services.AddSingleton(new NodeTeamRoster(roster));
        services.AddSingleton<IOperationSigner>(signer);
        services.AddTestAuthorizationGate();
        services.AddSingleton<IAuthorizedAuditTrail>(new InMemoryAuditTrail());
        // Ticket 290 — the roster's revocation authority writes the administrator removal in the same unit of
        // work, so the durable administrator authority is now a prerequisite of the roster composition (the
        // production host registers it in Program.cs before AddNodeRoster).
        services.AddSingleton(sp => new NodeAdministratorAuthority(
            sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>(),
            TimeProvider.System,
            sp.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>()));
        services.AddNodeRoster();

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<CompromisedDeviceResponseService>(
            provider.GetRequiredService<ICompromisedDeviceResponseService>());
        Assert.IsType<NodeRosterCompromisedDeviceRevocationPublisher>(
            provider.GetRequiredService<ICompromisedDeviceRevocationPublisher>());
        Assert.IsType<NodeRosterCompromisedDeviceResponseStore>(
            provider.GetRequiredService<ICompromisedDeviceResponseStore>());
        Assert.IsType<DeferredCompromiseKeyRotation>(
            provider.GetRequiredService<ICompromiseKeyRotation>());
    }
}
