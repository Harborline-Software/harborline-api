using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

public sealed class EmptyRecordStandingResolver : IRecordStandingResolver
{
    private static readonly IReadOnlyList<RecordStanding> Empty = Array.Empty<RecordStanding>();

    public ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(
        AuthorizationGateRequest request,
        IReadOnlySet<RoleReference> effectiveRecordRoles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effectiveRecordRoles);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Empty);
    }
}
