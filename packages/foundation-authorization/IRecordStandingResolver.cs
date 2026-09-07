using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

public interface IRecordStandingResolver
{
    ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(
        AuthorizationGateRequest request,
        IReadOnlySet<RoleReference> effectiveRecordRoles,
        CancellationToken ct = default);
}
