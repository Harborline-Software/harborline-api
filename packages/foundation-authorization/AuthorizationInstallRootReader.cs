using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Reads the principal's install-root grant operations without making an authorization verdict.
/// Roster reconstruction uses this lower-level grant fact reader; the authorization gate then combines
/// those grant facts with the independently verified roster facts without a dependency cycle.
/// </summary>
public interface IAuthorizationInstallRootReader
{
    /// <summary>Returns the operations conferred at the install root at the supplied instant.</summary>
    ValueTask<PermissionSet> ReadAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}

/// <summary>The kernel implementation backed by the canonical authorization closure snapshot.</summary>
public sealed class AuthorizationInstallRootReader(IAuthorizationClosureSnapshotReader closure)
    : IAuthorizationInstallRootReader
{
    /// <inheritdoc />
    public async ValueTask<PermissionSet> ReadAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:read@/"), principal, tenant,
            new AuthorizationTarget("tenant", tenant.Value, ScopeExpression.Parse("/")), at);
        var snapshot = await closure.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        return PermissionSet.From(snapshot.Derivations
            .Select(derivation => derivation.Atom)
            .Where(atom => atom.Scope.Value == "/")
            .Select(atom => atom.Operation.Value));
    }
}
