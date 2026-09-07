using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

public readonly record struct AuthorizationTarget(
    string RecordKind,
    string RecordId,
    ScopeExpression Scope);

public sealed record AuthorizationGateRequest(
    PermissionAtom Act,
    ActorId Principal,
    TenantId Tenant,
    AuthorizationTarget Target,
    DateTimeOffset At);

public sealed record AuthorizationAtomDerivation(
    PermissionAtom Atom,
    RoleReference Role,
    string GrantId,
    long GrantOwnerVersion,
    string DefinitionId,
    ScopeExpression GrantScope,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    // The reader's OWN in-force verdict for this binding at the decided instant — the outcome of the
    // window and revocation rule it applied when it admitted (or excluded) the grant. Null means the
    // producer computed no such fact; nothing downstream may re-derive it. Ticket 212 slice 1.
    bool? InForce = null);

public sealed record RecordStanding(
    RoleReference Role,
    string RuleId,
    string EvidenceVersion);
