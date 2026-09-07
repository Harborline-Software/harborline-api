using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Request-scoped authorization facade over one selected-session feature.
/// </summary>
/// <remarks>
/// The facade is bind-once inside the framework request scope. Before binding it is unresolved and
/// least-privilege, which also permits the ADR 0091 same-instance startup assertion to inspect the
/// request scope safely. Selected-session requests never read desktop authority; only an unbound
/// desktop-plane request may use the explicitly bridged operator context.
/// </remarks>
internal sealed class SelectedSessionTenantContext : Harborline.Api.Foundation.Authorization.ITenantContext
{
    private static readonly IReadOnlyList<string> NoRoles = Array.Empty<string>();
    private readonly ISelectedSessionPermissionResolver _permissionResolver;
    private readonly ActiveTeamAuthorizationContext? _desktopAuthorization;
    private SelectedSessionRequestPrincipal? _principal;
    private TenantMetadata? _tenant;
    private PermissionSet? _permissions;

    public SelectedSessionTenantContext(
        ISelectedSessionPermissionResolver permissionResolver,
        ActiveTeamAuthorizationContext? desktopAuthorization = null)
    {
        _permissionResolver = permissionResolver
            ?? throw new ArgumentNullException(nameof(permissionResolver));
        _desktopAuthorization = desktopAuthorization;
    }

    internal void Bind(SelectedSessionRequestPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (_principal is not null)
        {
            throw new InvalidOperationException(
                "A request authorization facade may bind exactly one immutable principal.");
        }

        _principal = principal;
        _tenant = new TenantMetadata
        {
            Id = principal.TenantId,
            Name = principal.TenantId.Value,
        };
        _permissions = null;
    }

    internal async ValueTask BindAsync(
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        Bind(principal);
        try
        {
            _permissions = await _permissionResolver
                .ResolveAsync(principal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _permissions = null;
        }
    }

    public TenantMetadata? Tenant => _tenant;

    // This foundation slice exposes the canonical Party as the authorization subject. The original
    // tenant principal remains available on the immutable feature for later PEP conversions.
    public string UserId => _principal?.CanonicalParty.Value ?? string.Empty;

    // Roles remain empty: ADR 0163 makes ShipRole authorization scope empty. PBAC is exposed only through
    // HasPermission, and comes from the signed roster edge after the grant liveness/epoch fence.
    public IReadOnlyList<string> Roles => NoRoles;

    /// <summary>Raw server-derived permissions for the selected session, or null while unresolved.</summary>
    internal IReadOnlyCollection<string>? EffectivePermissions => _permissions?.Permissions;

    public bool HasPermission(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (_principal is null)
        {
            if (NodeCallerAttributionScope.HasBoundWebPrincipal)
            {
                // Device-plane requests do not bind a selected web principal, but they still carry
                // an explicit non-desktop plane signal. Never borrow the desktop operator's grants.
                return false;
            }
            // Bootstrap bearer and legacy founder requests are the desktop plane. Preserve their
            // operator authority through this request-scoped inner facade.
            return _desktopAuthorization?.HasPermission(permission) == true;
        }
        return _permissions?.Contains(permission) == true;
    }
}
