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
    AdmittedInstant Instant)
{
    /// <summary>
    /// The decision instant. Server-admitted only (T-1015): there is no public path that sets it from a raw
    /// <see cref="DateTimeOffset"/>, so a caller cannot choose the time a decision rests on.
    /// </summary>
    public AdmittedInstant Instant { get; init; } = Instant ?? throw new ArgumentNullException(nameof(Instant));
    public DateTimeOffset At => Instant.Value;
    /// <summary>
    /// The roster facts the gate derived through <see cref="IAuthorizationRosterConstraintReader"/>, recorded
    /// on the decision's request. Only the gate sets it (T-519); routes and blocks cannot supply one.
    /// </summary>
    public AuthorizationRosterInputs? Roster { get; internal init; }
    /// <summary>Validated audit association only; never consulted by authorization policy.</summary>
    public Guid? CorrelationId { get; init; }
    /// <summary>
    /// The kernel's own refusal code, set only by the gate while evaluating live grant attenuation. Routes and
    /// blocks cannot set it (T-519); the gate also clears any value on entry.
    /// </summary>
    public string? GrantRefusal { get; internal init; }
    /// <summary>Server-read role atoms to attenuate against the actor's live grants at each atom's scope.</summary>
    public PermissionAtomSet? RequiredGrantAtoms { get; init; }
}

/// <summary>Server-derived membership facts; only the gate turns them into a verdict.</summary>
public sealed record AuthorizationRosterInputs(string PartyId, bool Member, bool Ejected)
{
    public bool? RegistryMember { get; init; }

    /// <summary>
    /// The Administrator role is ABOUT to be conferred on this subject (an in-flight handover), and no
    /// grant carries it yet. Ticket 294 slice 2a: the gate adds the atoms that role confers ONLY where
    /// <see cref="Member"/> is false — a subject with no roster edge is decided on what it is about to
    /// hold, a subject WITH a roster edge is decided on its own conferred grants, so a successor the
    /// signed roster has narrowed is refused instead of being handed a role that does nothing. Read by
    /// <c>AuthorizationGate</c> alone; a caller must never substitute the atoms itself.
    /// </summary>
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
