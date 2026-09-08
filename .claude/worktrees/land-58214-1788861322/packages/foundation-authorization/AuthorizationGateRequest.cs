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
    DateTimeOffset At)
{
    public AuthorizationRosterInputs? Roster { get; init; }
    /// <summary>A server-verified grant constraint failure; the gate retains it in its decision.</summary>
    public string? GrantRefusal { get; init; }
}

/// <summary>Server-derived membership facts; only the gate turns them into a verdict.</summary>
public sealed record AuthorizationRosterInputs(
    string PartyId, bool Member, bool Ejected, PermissionSet? Permissions)
{
    public bool? RegistryMember { get; init; }
    public bool ProspectiveAdministratorGrant { get; init; }
    public bool RequireMember { get; init; }
    public bool RequireGrantCoverage { get; init; }
    public PermissionSet RequiredPermissions { get; init; } = PermissionSet.Empty;
}

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
