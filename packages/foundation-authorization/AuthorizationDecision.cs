using System.Collections.Immutable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

public enum AuthorizationVerdict { Denied = 0, Allowed = 1 }

public enum AuthorizationResolutionStage
{
    ActKind = 0,
    EffectiveRecordRoles = 1,
    RecordStandings = 2,
    NamedRoleUnionVerdict = 3,
    Bootstrap = 4,
}

public sealed record AuthorizationResolutionStep
{
    public AuthorizationResolutionStep(
        AuthorizationResolutionStage stage,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        Stage = stage;
        Inputs = inputs.ToImmutableArray();
        Outputs = outputs.ToImmutableArray();
    }

    public AuthorizationResolutionStage Stage { get; }
    public IReadOnlyList<string> Inputs { get; }
    public IReadOnlyList<string> Outputs { get; }
}

public sealed class AuthorizationDecision
{
    internal static AuthorizationDecision CreateBootstrap(
        AuthorizationGateRequest request,
        string evidence) => new(
        request,
        AuthorizationVerdict.Allowed,
        [request.Act],
        [],
        [],
        [new AuthorizationResolutionStep(
            AuthorizationResolutionStage.Bootstrap,
            [$"principal:{request.Principal}", $"tenant:{request.Tenant}"],
            [$"bootstrap:{evidence}"])]);

    internal AuthorizationDecision(
        AuthorizationGateRequest request,
        AuthorizationVerdict verdict,
        IReadOnlyList<PermissionAtom> atomsConsidered,
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        IReadOnlyList<RecordStanding> standings,
        IReadOnlyList<AuthorizationResolutionStep> resolutionTrace,
        IReadOnlyList<AuthorizationExcludedBinding>? excludedBindings = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(atomsConsidered);
        ArgumentNullException.ThrowIfNull(derivations);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(resolutionTrace);
        Request = request;
        Verdict = verdict;
        AtomsConsidered = atomsConsidered.ToImmutableArray();
        Derivations = derivations.ToImmutableArray();
        Standings = standings.ToImmutableArray();
        Resolution = resolutionTrace.ToImmutableArray();
        ExcludedBindings = (excludedBindings ?? []).ToImmutableArray();
        DecidedAt = request.At;
        // Ticket 212 slice 1: built HERE, from the values this decision already computed, so a decision
        // without evidence cannot exist. Nothing is re-evaluated and no store is read.
        Evidence = AuthorizationDecisionEvidence.ForGate(request, verdict, Derivations, Standings, ExcludedBindings);
    }

    public AuthorizationGateRequest Request { get; }
    public AuthorizationVerdict Verdict { get; }
    public IReadOnlyList<PermissionAtom> AtomsConsidered { get; }
    public IReadOnlyList<AuthorizationAtomDerivation> Derivations { get; }
    public IReadOnlyList<RecordStanding> Standings { get; }
    public IReadOnlyList<AuthorizationResolutionStep> Resolution { get; }

    /// <summary>The bindings the closure reader excluded from this read, each with its reason (ticket 212).</summary>
    public IReadOnlyList<AuthorizationExcludedBinding> ExcludedBindings { get; }
    public DateTimeOffset DecidedAt { get; }

    /// <summary>
    /// The one structured evidence object this verdict carries (ticket 212, ledger L650). Never null:
    /// it is built by the only constructor. <see cref="AuthorizationDecisionEvidence.Project"/> turns it
    /// into the four ordered public steps.
    /// </summary>
    public AuthorizationDecisionEvidence Evidence { get; }

    /// <summary>Returns this allowed decision or raises the canonical denial before later stages run.</summary>
    public AuthorizationDecision RequireAllowed()
    {
        if (Verdict is AuthorizationVerdict.Denied)
            throw new AuthorizationDeniedException(this);
        return this;
    }

    /// <summary>
    /// Verifies that a synchronous reaction is carrying the exact decision that admitted the record it
    /// is about to mutate. This validates evidence only; it never re-decides authorization.
    /// </summary>
    public AuthorizationDecision RequireAllowedReaction(
        AuthorizationOperation operation,
        TenantId tenant,
        string recordKind,
        string recordId)
    {
        RequireAllowed();
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        if (string.IsNullOrWhiteSpace(Request.Principal.Value)
            || Request.Act.Operation != operation
            || Request.Tenant != tenant
            || !string.Equals(Request.Target.RecordKind, recordKind, StringComparison.Ordinal)
            || !string.Equals(Request.Target.RecordId, recordId, StringComparison.Ordinal)
            || DecidedAt != Request.At)
            throw new ArgumentException(
                "The originating authorization decision does not correspond to the reaction target.",
                nameof(recordId));
        return this;
    }
}
