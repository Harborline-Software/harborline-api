using System.Collections.Immutable;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>Which deciding path produced an evidence object (ticket 212, ledger L650).</summary>
public enum AuthorizationEvidenceKind
{
    /// <summary>The authorization gate (ticket 205).</summary>
    Gate = 0,

    /// <summary>The separation-of-duty engine (ticket 206).</summary>
    SeparationOfDuty = 1,
}

/// <summary>
/// The shape of a refusal, classified from the values the decision ALREADY computed. This is a reading of
/// one decision, never a second evaluation: it consults no store, no clock and no ambient scope, and it
/// cannot change the verdict it describes.
/// </summary>
public enum AuthorizationRefusalShape
{
    /// <summary>The act was allowed.</summary>
    None = 0,

    /// <summary>Nothing bound the principal to the record — no derivation and no standing.</summary>
    NoEffectiveRole = 1,

    /// <summary>A binding exists but its grant scope does not contain the target scope.</summary>
    GrantNarrowed = 2,

    /// <summary>Every binding on record was outside its validity window at the decided instant.</summary>
    ValidityLapsed = 3,

    /// <summary>The operation is held in force, but not at a scope covering the requested act.</summary>
    ScopeMismatch = 4,

    /// <summary>Roles are held in force, but none of them carries the requested operation.</summary>
    OperationNotHeld = 5,

    /// <summary>The principal appears among its own approvers and no override named an overrider.</summary>
    SeparationOfDutyConflict = 6,

    /// <summary>The approval was refused for a reason that is not a duty conflict.</summary>
    ApprovalRefused = 7,
}

/// <summary>One effective role, with the scope and dates and the binding or grant it came from.</summary>
public sealed record AuthorizationEvidenceRole(
    string Role,
    string Scope,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    string BindingId,
    long? BindingVersion,
    string DefinitionId,
    string Atom,
    // The reader's own in-force verdict, carried through; null when it computed none.
    bool? InForce,
    bool Deciding);

/// <summary>One computed record standing, with the rule and evidence version it was computed from.</summary>
public sealed record AuthorizationEvidenceStanding(string Role, string RuleId, string EvidenceVersion);

/// <summary>One public resolution step. Four of these, in order, are the whole public trace.</summary>
public sealed record AuthorizationTraceStep(int Ordinal, string Stage, IReadOnlyList<string> Facts);

/// <summary>
/// The ONE structured evidence object every verdict carries (ticket 212 slice 1, ledger L648/L650). It is
/// built inside the decision constructor from the values that decision already computed, so a decision
/// without evidence cannot be constructed, and it is projected — purely — into exactly four ordered public
/// steps by <see cref="Project"/>.
/// </summary>
public sealed record AuthorizationDecisionEvidence
{
    /// <summary>The stable stage names of the four public steps, in order.</summary>
    public const string ActStage = "act";

    /// <summary>Stage two — the effective roles and the deciding binding or grant.</summary>
    public const string EffectiveRolesStage = "effective-roles";

    /// <summary>Stage three — the computed standings.</summary>
    public const string StandingsStage = "standings";

    /// <summary>Stage four — the verdict.</summary>
    public const string VerdictStage = "verdict";

    /// <summary>The evidence schema version. Bumped when the projected step shape changes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The number of public steps <see cref="Project"/> always returns.</summary>
    public const int StepCount = 4;

    private AuthorizationDecisionEvidence(
        AuthorizationEvidenceKind kind,
        string act,
        string principal,
        string tenant,
        string target,
        DateTimeOffset at,
        bool allowed,
        AuthorizationRefusalShape refusal,
        string decidingBinding,
        ImmutableArray<AuthorizationEvidenceRole> roles,
        ImmutableArray<AuthorizationEvidenceStanding> standings,
        ImmutableArray<string> approvals,
        PermissionAtom actAtom,
        ImmutableArray<AuthorizationAtomDerivation> bindings,
        ImmutableArray<AuthorizationExcludedBinding> excluded)
    {
        Kind = kind;
        Act = act;
        Principal = principal;
        Tenant = tenant;
        Target = target;
        At = at;
        Allowed = allowed;
        Refusal = refusal;
        DecidingBinding = decidingBinding;
        Roles = roles;
        Standings = standings;
        Approvals = approvals;
        ActAtom = actAtom;
        Bindings = bindings;
        Excluded = excluded;
    }

    /// <summary>The evidence schema version this object was built at.</summary>
    public int Version => CurrentVersion;

    /// <summary>Which deciding path produced this evidence.</summary>
    public AuthorizationEvidenceKind Kind { get; }

    /// <summary>The act, spelled as its scoped permission atom.</summary>
    public string Act { get; }

    /// <summary>The principal the verdict was decided for.</summary>
    public string Principal { get; }

    /// <summary>The tenant the act was scoped to.</summary>
    public string Tenant { get; }

    /// <summary>The record target, or <c>install-wide</c> when the act carries none.</summary>
    public string Target { get; }

    /// <summary>The decided instant, always the request instant.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Whether the act was allowed.</summary>
    public bool Allowed { get; }

    /// <summary>The classified refusal shape; <see cref="AuthorizationRefusalShape.None"/> when allowed.</summary>
    public AuthorizationRefusalShape Refusal { get; }

    /// <summary>The binding, grant, standing rule or limit source the verdict turned on.</summary>
    public string DecidingBinding { get; }

    /// <summary>The effective roles, each with its scope, dates and binding.</summary>
    public IReadOnlyList<AuthorizationEvidenceRole> Roles { get; }

    /// <summary>The computed record standings.</summary>
    public IReadOnlyList<AuthorizationEvidenceStanding> Standings { get; }

    /// <summary>The approval facts of a separation-of-duty decision; empty for a gate decision.</summary>
    public IReadOnlyList<string> Approvals { get; }

    /// <summary>The act as the typed atom the decision compared against; the counterfactual reads it.</summary>
    internal PermissionAtom ActAtom { get; }

    /// <summary>
    /// The effective bindings exactly as the decision received them (ticket 212 slice 2). Every entry is in
    /// force at <see cref="At"/> by construction — the closure reader excluded the rest — so nothing here
    /// re-derives a temporal fact.
    /// </summary>
    public IReadOnlyList<AuthorizationAtomDerivation> Bindings { get; }

    /// <summary>
    /// The bindings the closure reader found on record but excluded, each with its reason. A lapse reaches
    /// the gate as an absence; this is the only place the evidence can see one.
    /// </summary>
    public IReadOnlyList<AuthorizationExcludedBinding> Excluded { get; }

    /// <summary>
    /// Returns this evidence with a different binding set and the verdict that binding set produces. Used
    /// ONLY by <see cref="AuthorizationCounterfactual"/> to apply a named change to the evidence — never to
    /// the system, and never on a path that records a verdict.
    /// </summary>
    internal AuthorizationDecisionEvidence WithBindings(
        IReadOnlyList<AuthorizationAtomDerivation> bindings,
        IReadOnlyList<AuthorizationExcludedBinding> excluded)
    {
        var allowed = Allows(bindings, ActAtom);
        // Ticket 212 slice 3: the applied evidence's steps two and four must agree with its own binding set.
        // They used to be literals -- an evidence flipped toward allow projected `deciding:none` beside
        // `verdict:allowed`, and one flipped toward refusal always said `NoEffectiveRole` even when the
        // binding was merely narrowed. Both are read from the same expressions the recorded decision used.
        var decidingIndex = -1;
        for (var index = 0; index < bindings.Count && decidingIndex < 0; index++)
        {
            if (bindings[index].Atom.Covers(ActAtom))
                decidingIndex = index;
        }

        return new AuthorizationDecisionEvidence(
            Kind,
            Act,
            Principal,
            Tenant,
            Target,
            At,
            allowed,
            allowed ? AuthorizationRefusalShape.None : Classify(ActAtom, bindings, Standings.Count),
            allowed && decidingIndex >= 0
                ? $"grant:{bindings[decidingIndex].GrantId}@{bindings[decidingIndex].GrantOwnerVersion}"
                : "none",
            [.. bindings.Select((item, index) => new AuthorizationEvidenceRole(
                item.Role.ToString(),
                item.GrantScope.ToString(),
                item.ValidFrom,
                item.ValidUntil,
                item.GrantId,
                item.GrantOwnerVersion,
                item.DefinitionId,
                item.Atom.ToString(),
                true,
                allowed && index == decidingIndex))],
            [.. Standings],
            [.. Approvals],
            ActAtom,
            [.. bindings],
            [.. excluded]);
    }

    /// <summary>
    /// The gate's own verdict line — <c>atoms.Any(atom =&gt; atom.Covers(act))</c> — read over the evidence's
    /// bindings. This is a restatement, not a second evaluator: it consults no store and no clock, and
    /// <see cref="AuthorizationCounterfactual"/> offers nothing unless it reproduces the recorded verdict.
    /// </summary>
    internal static bool Allows(IReadOnlyList<AuthorizationAtomDerivation> bindings, PermissionAtom act) =>
        bindings.Any(item => item.Atom.Covers(act));

    /// <summary>
    /// Projects the evidence into exactly <see cref="StepCount"/> ordered public steps: act kind; effective
    /// roles with scope, dates and the deciding binding or grant; computed standings; verdict. Pure — it
    /// reads only this object.
    /// </summary>
    public IReadOnlyList<AuthorizationTraceStep> Project()
    {
        IReadOnlyList<string> bindings = Kind is AuthorizationEvidenceKind.SeparationOfDuty
            ? Approvals
            : Roles.Select(Describe).ToArray();
        var standings = Standings
            .Select(item => $"role:{item.Role};rule:{item.RuleId};evidence:{item.EvidenceVersion}")
            .ToArray();

        return
        [
            new AuthorizationTraceStep(1, ActStage,
                [
                    $"kind:{Kind}",
                    $"act:{Act}",
                    $"target:{Target}",
                    $"principal:{Principal}",
                    $"tenant:{Tenant}",
                    $"at:{At:O}",
                ]),
            new AuthorizationTraceStep(2, EffectiveRolesStage,
                [
                    .. bindings.Count == 0 ? ["roles:none"] : bindings,
                    $"deciding:{DecidingBinding}",
                ]),
            new AuthorizationTraceStep(3, StandingsStage,
                [.. standings.Length == 0 ? ["standings:none"] : standings]),
            new AuthorizationTraceStep(4, VerdictStage,
                [
                    $"verdict:{(Allowed ? "allowed" : "denied")}",
                    $"refusal:{Refusal}",
                    $"version:{Version}",
                ]),
        ];
    }

    private static string Describe(AuthorizationEvidenceRole role) =>
        $"role:{role.Role};scope:{role.Scope};valid:{role.ValidFrom:O}..{(role.ValidUntil is { } end ? end.ToString("O") : "open")}"
        + $";binding:{role.BindingId}@{(role.BindingVersion is { } version ? version.ToString() : "-")}"
        + $";definition:{role.DefinitionId};atom:{role.Atom};in-force:{InForceFact(role.InForce)}"
        + (role.Deciding ? ";deciding" : string.Empty);

    /// <summary>
    /// Builds the gate's evidence from the values <see cref="AuthorizationGate"/> already computed. Called
    /// from the <see cref="AuthorizationDecision"/> constructor and nowhere else.
    /// </summary>
    internal static AuthorizationDecisionEvidence ForGate(
        AuthorizationGateRequest request,
        AuthorizationVerdict verdict,
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        IReadOnlyList<RecordStanding> standings,
        IReadOnlyList<AuthorizationExcludedBinding> excluded)
    {
        var at = request.At;
        var allowed = verdict is AuthorizationVerdict.Allowed;
        // Deciding is identified by POSITION, not by object identity or value: two equal derivations must not
        // both be marked deciding, and a re-materialised equal derivation must behave the same way.
        var decidingIndex = -1;
        for (var index = 0; index < derivations.Count && decidingIndex < 0; index++)
        {
            if (derivations[index].Atom.Covers(request.Act))
                decidingIndex = index;
        }

        var deciding = decidingIndex < 0 ? null : derivations[decidingIndex];
        var roles = derivations
            .Select((item, index) => new AuthorizationEvidenceRole(
                item.Role.ToString(),
                item.GrantScope.ToString(),
                item.ValidFrom,
                item.ValidUntil,
                item.GrantId,
                item.GrantOwnerVersion,
                item.DefinitionId,
                item.Atom.ToString(),
                item.InForce,
                allowed && index == decidingIndex))
            .ToImmutableArray();
        var decidingBinding = (allowed, deciding, standings.Count) switch
        {
            (true, { } grant, _) => $"grant:{grant.GrantId}@{grant.GrantOwnerVersion}",
            (true, null, > 0) => $"standing:{standings[0].RuleId}@{standings[0].EvidenceVersion}",
            (true, null, _) => "bootstrap",
            _ => "none",
        };

        return new AuthorizationDecisionEvidence(
            AuthorizationEvidenceKind.Gate,
            request.Act.ToString(),
            request.Principal.Value,
            request.Tenant.Value,
            string.IsNullOrWhiteSpace(request.Target.RecordKind)
                ? "install-wide"
                : $"{request.Target.RecordKind}/{request.Target.RecordId}@{request.Target.Scope}",
            at,
            allowed,
            allowed ? AuthorizationRefusalShape.None : Classify(request.Act, derivations, standings.Count),
            decidingBinding,
            roles,
            [.. standings.Select(item => new AuthorizationEvidenceStanding(
                item.Role.ToString(), item.RuleId, item.EvidenceVersion))],
            [],
            request.Act,
            [.. derivations],
            [.. excluded]);
    }

    /// <summary>
    /// Classifies a denial from the derivations and standings the gate already read, reading each
    /// binding's in-force fact as the reader computed it — never re-deriving one. The order matters: a
    /// binding that lapsed is reported as a lapse rather than as the absence it looks like downstream, and
    /// a narrowed grant is reported as narrowing rather than as a scope mismatch — these are the three
    /// changes ticket 212's counterfactual (slice 2) is allowed to propose.
    /// </summary>
    private static AuthorizationRefusalShape Classify(
        PermissionAtom act,
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        int standingCount)
    {
        if (derivations.Count == 0 && standingCount == 0)
            return AuthorizationRefusalShape.NoEffectiveRole;
        if (derivations.Count > 0 && derivations.All(item => item.InForce is false))
            return AuthorizationRefusalShape.ValidityLapsed;

        var operationHeld = derivations
            .Where(item => item.InForce is not false && item.Atom.Operation.Equals(act.Operation))
            .ToArray();
        if (operationHeld.Any(item => !item.GrantScope.Contains(act.Scope)))
            return AuthorizationRefusalShape.GrantNarrowed;
        return operationHeld.Length > 0
            ? AuthorizationRefusalShape.ScopeMismatch
            : AuthorizationRefusalShape.OperationNotHeld;
    }

    private static string InForceFact(bool? inForce) =>
        inForce switch { true => "yes", false => "no", null => "not-computed" };

    /// <summary>
    /// Builds the separation-of-duty engine's evidence from the facts that decision already carries. Called
    /// from the <see cref="SeparationOfDuty.SeparationOfDutyDecision"/> constructor and nowhere else.
    /// </summary>
    internal static AuthorizationDecisionEvidence ForApproval(
        PermissionAtom act,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        bool approved,
        bool conflicted,
        string refusalReason,
        string outcome,
        string policy,
        decimal appliedLimit,
        string limitSource,
        string? postingPeriodState,
        IReadOnlyList<ActorId> approvers) =>
        new(
            AuthorizationEvidenceKind.SeparationOfDuty,
            act.ToString(),
            principal.Value,
            tenant.Value,
            "approval",
            at,
            approved,
            approved
                ? AuthorizationRefusalShape.None
                : conflicted
                    ? AuthorizationRefusalShape.SeparationOfDutyConflict
                    : AuthorizationRefusalShape.ApprovalRefused,
            $"limit-source:{limitSource};policy:{policy};limit:{appliedLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            [],
            [
                new AuthorizationEvidenceStanding(
                    $"separation-of-duty:{outcome}", refusalReason, postingPeriodState ?? "unresolved"),
            ],
            [.. approvers.Select(approver => $"approver:{approver.Value}")],
            act,
            [],
            []);
}
