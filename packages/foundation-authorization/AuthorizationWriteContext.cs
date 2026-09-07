using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>The server-derived authority facts carried through one write pipeline.</summary>
public readonly record struct AuthorizationWriteContext(
    ActorId Principal,
    TenantId Tenant,
    DateTimeOffset At)
{
    /// <summary>Builds a request from a coordinator-derived operation and record identity.</summary>
    public AuthorizationGateRequest Request(
        AuthorizationOperation operation,
        string recordKind,
        string recordId)
    {
        var scope = AuthorizationGate.CanonicalTargetScope(Tenant, recordKind, recordId);
        return new AuthorizationGateRequest(
            new PermissionAtom(operation, scope),
            Principal,
            Tenant,
            new AuthorizationTarget(recordKind, recordId, scope),
            At);
    }

    /// <summary>
    /// Builds a request for an operation declared install-wide (ledger L600/L671): no record target, resolved
    /// against the install root. The gate refuses this shape for any operation the definition side has not
    /// declared install-wide, so this is a request builder and not an authorization decision.
    /// </summary>
    public AuthorizationGateRequest InstallWide(AuthorizationOperation operation)
    {
        var scope = AuthorizationGate.InstallWideScope;
        return new AuthorizationGateRequest(
            new PermissionAtom(operation, scope),
            Principal,
            Tenant,
            new AuthorizationTarget(string.Empty, string.Empty, scope),
            At);
    }
}
