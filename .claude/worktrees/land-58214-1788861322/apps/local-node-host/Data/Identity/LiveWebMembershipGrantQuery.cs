using System.Linq.Expressions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// The single translatable live-grant predicate shared by web membership admission, first-wire atlas admission,
/// and the R2 mode selector. Keeping one expression prevents a look-alike query from silently weakening the
/// grant liveness boundary.
/// </summary>
internal static class LiveWebMembershipGrantQuery
{
    internal static Expression<Func<GrantRow, bool>> ForTenantAt(TenantId tenant, long nowUnixMs) =>
        row =>
            row.TenantId == tenant.Value &&
            row.RevokedAtUnixMs == null &&
            row.ValidityFromUnixMs <= nowUnixMs &&
            (row.ValidityUntilUnixMs == null || row.ValidityUntilUnixMs > nowUnixMs);
}
