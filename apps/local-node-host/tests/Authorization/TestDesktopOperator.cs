using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 294 slice 3b — the desktop actor a test composes. There is no compile-time operator id any more: the
/// desktop actor is the roster party bound to the node's signing key. This fixture is one node key self-admitted
/// under <see cref="Principal"/>, so a composition that registers it has a desktop actor with that value.
/// </summary>
internal static class TestDesktopOperator
{
    /// <summary>The test node's roster party, standing in for the founder's canonical tenant principal.</summary>
    internal const string Principal = "desktop-operator-under-test";

    private static readonly KeyPair Key = KeyPair.Generate();

    internal static ActorId Actor { get; } = new(Principal);

    internal static PartyId Party { get; } = new(Principal);

    internal static IOperationSigner Signer { get; } = new Ed25519Signer(Key);

    /// <summary>A fresh live roster whose genesis admits the test node's key as <see cref="Principal"/>.</summary>
    internal static NodeTeamRoster Roster(Guid? team = null) => new(MemberRoster.Genesis(
        team ?? Guid.Parse("7e57eeee-0000-0000-0000-000000000294"), Principal, Signer, new Ed25519Verifier(),
        DateTimeOffset.UnixEpoch, Guid.Parse("29400000-0000-4000-8000-0000000000d0")));

    /// <summary>
    /// The desktop actor for a composition. Only the identity is registered, over its own roster and key, so a
    /// composition's own roster or signer (and whatever reads them) is left exactly as it was.
    /// </summary>
    internal static IServiceCollection AddTestDesktopOperator(this IServiceCollection services)
    {
        services.TryAddSingleton(_ => Identity());
        return services;
    }

    /// <summary>
    /// The desktop actor a real composed host mints on first boot from <paramref name="rootSeedHex"/>: the founder's
    /// canonical tenant principal in the seed-derived genesis tenant (Program.cs, ticket 294 slice 2a).
    /// </summary>
    internal static string HostOperator(string rootSeedHex) =>
        FounderTenantMembershipAttachService.DerivePrincipal(
            Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(
                GenesisTeamId.Derive(Convert.FromHexString(rootSeedHex))),
            InstallationFounderBootstrapCeremony.CorrelationId).Value;

    /// <summary>The identity alone, for code that takes one directly.</summary>
    internal static NodeOperatorIdentity Identity() => new(Roster(), Signer);
}
