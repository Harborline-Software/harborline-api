using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

internal static class GrantStoreTestExtensions
{
    public static Task<AccessGrant> SaveAsync(
        this IGrantStore store, TenantId tenantId, AccessGrant grant, long expectedOwnerVersion,
        CancellationToken ct = default) => store.AppendAsync(tenantId, grant, ct: ct);

    public static Task<AccessGrant?> RevokeAsync(
        this IGrantStore store, TenantId tenantId, GrantId grantId, long expectedOwnerVersion,
        DateTimeOffset revokedAt, CancellationToken ct = default) => store.RevokeAsync(
            tenantId, grantId,
            new GrantRevocation(new ActorId("test-revoker"), revokedAt,
                new GrantReason(GrantReasonCodes.RevocationOffboarding, "test-fixture")), ct);
}
