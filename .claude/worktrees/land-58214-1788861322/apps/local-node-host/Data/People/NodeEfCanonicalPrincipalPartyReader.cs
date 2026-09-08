using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.People;

/// <summary>Durable explicit-tenant adapter to People-owned Party rows.</summary>
public sealed class NodeEfCanonicalPrincipalPartyReader(NodeEfPartyRepository repository)
    : ICanonicalPrincipalPartyReader
{
    private readonly NodeEfPartyRepository _repository = repository
        ?? throw new ArgumentNullException(nameof(repository));

    /// <inheritdoc />
    public async ValueTask<CanonicalPartyBinding?> ResolveAsync(
        TenantId tenant,
        PrincipalUserId user,
        CancellationToken cancellationToken = default)
    {
        if (tenant == default)
            return null;

        var party = await _repository
            .ResolvePrincipalPartyAsync(tenant, user.Value, cancellationToken)
            .ConfigureAwait(false);

        return party is null
            ? null
            : new CanonicalPartyBinding(
                tenant,
                user,
                new CanonicalPartyReference(party.Id.Value));
    }
}
