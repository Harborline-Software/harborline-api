using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

internal sealed class AcceptanceInviterRoster(string tenant, string inviterParty, DateTimeOffset at)
    : IVerifiedTenantRosterReader
{
    public Task<MemberRoster> ReadAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        using var key = KeyPair.Generate();
        return Task.FromResult(MemberRoster.Genesis(Guid.Parse(tenant), inviterParty,
            new Ed25519Signer(key), new Ed25519Verifier(), at, Guid.NewGuid()));
    }
}
