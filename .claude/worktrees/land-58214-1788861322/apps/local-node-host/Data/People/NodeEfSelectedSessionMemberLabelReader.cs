using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Data.People;

/// <summary>
/// Durable explicit-tenant adapter that reads ONE People-owned display label for a principal.
/// </summary>
/// <remarks>
/// Reuses the same tenant-scoped principal-to-Party join the canonical reader uses
/// (<see cref="NodeEfPartyRepository.ResolvePrincipalPartyAsync"/>), so this adds no second source of
/// truth for who a principal is — it returns the name the canonical reader discards. Missing,
/// duplicate, detached, tombstoned and wrong-tenant rows all resolve to null, which the identity
/// authority renders as an absent name rather than a substitute one.
/// </remarks>
internal sealed class NodeEfSelectedSessionMemberLabelReader(NodeEfPartyRepository repository)
    : ISelectedSessionMemberLabelReader
{
    private readonly NodeEfPartyRepository _repository = repository
        ?? throw new ArgumentNullException(nameof(repository));

    /// <inheritdoc />
    public async Task<SelectedSessionMemberLabel?> ReadAsync(
        TenantId tenant,
        PrincipalUserId principal,
        CancellationToken cancellationToken = default)
    {
        if (tenant == default)
        {
            return null;
        }

        var party = await _repository
            .ResolvePrincipalPartyAsync(tenant, principal.Value, cancellationToken)
            .ConfigureAwait(false);

        return party is null
            ? null
            : new SelectedSessionMemberLabel(
                new CanonicalPartyReference(party.Id.Value),
                party.DisplayName);
    }
}
