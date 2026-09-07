using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// The three changes ticket 212 allows a counterfactual to name (ledger L649/L693). Changing the person is
/// not one of them and there is no member for it.
/// </summary>
public enum AuthorizationCounterfactualKind
{
    /// <summary>No single change of the three would alter this verdict.</summary>
    None = 0,

    /// <summary>The scope of the deciding binding.</summary>
    BindingNarrowing = 1,

    /// <summary>The existence of the grant behind the deciding binding.</summary>
    GrantRevocation = 2,

    /// <summary>The validity window of the deciding binding.</summary>
    ValidityLapse = 3,
}

/// <summary>
/// The minimal verdict-changing counterfactual, derived from ONE decision's evidence (ticket 212 slice 2,
/// ledger L605/L649/L693).
///
/// It is a pure function over <see cref="AuthorizationDecisionEvidence"/>: no store, no clock, no ambient
/// scope, no second evaluation of the act. It names exactly one change, always one of the three kinds above,
/// and <see cref="ApplyTo"/> applies that change TO THE EVIDENCE — never to the system — so a caller can see
/// step four flip without anything having been re-decided.
/// </summary>
public sealed record AuthorizationCounterfactual(
    int Version,
    AuthorizationCounterfactualKind Kind,
    string Direction,
    string Binding,
    int BindingOrdinal,
    string Description)
{
    /// <summary>The counterfactual schema version. Bumped when this shape changes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The direction a named change would move the verdict in.</summary>
    public const string TowardRefusal = "refuse";

    /// <summary>The direction a named change would move the verdict in.</summary>
    public const string TowardAllow = "allow";

    /// <summary>No change of the three would move the verdict.</summary>
    public const string NoDirection = "none";

    /// <summary>The one counterfactual that names no change.</summary>
    public static AuthorizationCounterfactual None(string because) =>
        new(CurrentVersion, AuthorizationCounterfactualKind.None, NoDirection, "none", -1, because);

    /// <summary>Derives the minimal counterfactual from a decision's evidence. Reads nothing else.</summary>
    public static AuthorizationCounterfactual From(AuthorizationDecisionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind is not AuthorizationEvidenceKind.Gate)
        {
            return None(
                "a separation-of-duty verdict turns on approvals, not on a binding, grant or validity window");
        }

        // The restatement guard: the counterfactual only speaks when the gate's own verdict line, read over
        // this evidence, reproduces the recorded verdict. A standing-backed or bootstrap allow does not, and
        // gets "none" rather than a fabricated change.
        if (AuthorizationDecisionEvidence.Allows(evidence.Bindings, evidence.ActAtom) != evidence.Allowed)
            return None("the verdict does not rest on an effective binding");

        // Ticket 212 slice 3: the gate also allows on standing-derived atoms. With a standing present, a
        // binding that covers the act is not necessarily the only thing holding the allow up, so no single
        // binding change can be claimed to flip it. Minimality is over everything the gate read, not over
        // the bindings alone.
        if (evidence.Allowed && evidence.Standings.Count > 0)
            return None("a computed standing also covers this act; no single binding change would refuse it");

        return evidence.Allowed ? FromAllow(evidence) : FromRefusal(evidence);
    }

    /// <summary>
    /// Applies this change to <paramref name="evidence"/> and returns the changed evidence. Nothing is
    /// written, nothing is re-read: the returned object is a reading of "what the same decision would have
    /// recorded had this one thing been different".
    /// </summary>
    public AuthorizationDecisionEvidence ApplyTo(AuthorizationDecisionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (Kind is AuthorizationCounterfactualKind.None)
            return evidence;

        return string.Equals(Direction, TowardRefusal, StringComparison.Ordinal)
            ? ApplyTowardRefusal(evidence)
            : ApplyTowardAllow(evidence);
    }

    private static AuthorizationCounterfactual FromAllow(AuthorizationDecisionEvidence evidence)
    {
        // Identified by POSITION, never by object identity or record equality: two equal bindings must not
        // collapse into one, and a re-materialised equal binding must behave the same way.
        var covering = evidence.Bindings
            .Select((item, ordinal) => (item, ordinal))
            .Where(entry => entry.item.Atom.Covers(evidence.ActAtom))
            .ToArray();
        if (covering.Length != 1)
        {
            return None(
                $"{covering.Length} bindings independently cover this act; no single change would refuse it");
        }

        var (deciding, decidingOrdinal) = covering[0];
        var name = Name(deciding);
        // Least invasive first: a scope that can still shrink, then a window that can be pulled back, then
        // the grant itself. A grant at the exact act scope with an open window can only be revoked.
        if (!deciding.Atom.Scope.Equals(evidence.ActAtom.Scope))
        {
            return new AuthorizationCounterfactual(
                CurrentVersion,
                AuthorizationCounterfactualKind.BindingNarrowing,
                TowardRefusal,
                name,
                decidingOrdinal,
                $"narrowing binding {name} below {evidence.ActAtom.Scope} would refuse this act");
        }

        return deciding.ValidUntil is not null
            ? new AuthorizationCounterfactual(
                CurrentVersion,
                AuthorizationCounterfactualKind.ValidityLapse,
                TowardRefusal,
                name,
                decidingOrdinal,
                $"letting binding {name} lapse before {evidence.At:O} would refuse this act")
            : new AuthorizationCounterfactual(
                CurrentVersion,
                AuthorizationCounterfactualKind.GrantRevocation,
                TowardRefusal,
                name,
                decidingOrdinal,
                $"revoking grant {name} would refuse this act");
    }

    private static AuthorizationCounterfactual FromRefusal(AuthorizationDecisionEvidence evidence)
    {
        // A lapse or a revocation reaches the gate as an absence; the excluded bindings the closure reader
        // recorded are the only evidence that one happened.
        var lapsedOrdinal = Ordinal(evidence.Excluded, AuthorizationExclusionReason.ValidityLapsed, evidence.ActAtom);
        if (lapsedOrdinal >= 0)
        {
            var lapsed = evidence.Excluded[lapsedOrdinal];
            var name = Name(lapsed.Binding);
            return new AuthorizationCounterfactual(
                CurrentVersion,
                AuthorizationCounterfactualKind.ValidityLapse,
                TowardAllow,
                name,
                lapsedOrdinal,
                $"binding {name} lapsed at {lapsed.Binding.ValidUntil:O}; a validity window covering "
                + $"{evidence.At:O} would allow this act");
        }

        var revokedOrdinal = Ordinal(evidence.Excluded, AuthorizationExclusionReason.GrantRevoked, evidence.ActAtom);
        if (revokedOrdinal >= 0)
        {
            var revoked = evidence.Excluded[revokedOrdinal];
            var name = Name(revoked.Binding);
            return new AuthorizationCounterfactual(
                CurrentVersion,
                AuthorizationCounterfactualKind.GrantRevocation,
                TowardAllow,
                name,
                revokedOrdinal,
                $"grant {name} was revoked; restoring it would allow this act");
        }

        var narrowedOrdinal = -1;
        for (var index = 0; index < evidence.Bindings.Count && narrowedOrdinal < 0; index++)
        {
            if (evidence.Bindings[index].Atom.Operation.Equals(evidence.ActAtom.Operation))
                narrowedOrdinal = index;
        }

        if (narrowedOrdinal >= 0)
        {
            var narrowed = evidence.Bindings[narrowedOrdinal];
            var name = Name(narrowed);
            return new AuthorizationCounterfactual(
                CurrentVersion,
                AuthorizationCounterfactualKind.BindingNarrowing,
                TowardAllow,
                name,
                narrowedOrdinal,
                $"binding {name} is narrowed to {narrowed.Atom.Scope}; widening it to include "
                + $"{evidence.ActAtom.Scope} would allow this act");
        }

        return None("no binding narrowing, grant revocation or validity lapse would allow this act");
    }

    private AuthorizationDecisionEvidence ApplyTowardRefusal(AuthorizationDecisionEvidence evidence)
    {
        var deciding = evidence.Bindings[BindingOrdinal];
        var remaining = evidence.Bindings.Where((_, index) => index != BindingOrdinal).ToArray();
        // Narrowing "below the act scope" is not a scope this install has, so the applied evidence does not
        // invent one: the binding simply stops covering the act, which is the whole of what narrowing does
        // to this verdict. The Description already names the scope it would be narrowed below.
        if (Kind is AuthorizationCounterfactualKind.BindingNarrowing)
            return evidence.WithBindings(remaining, evidence.Excluded);

        var reason = Kind is AuthorizationCounterfactualKind.ValidityLapse
            ? AuthorizationExclusionReason.ValidityLapsed
            : AuthorizationExclusionReason.GrantRevoked;
        // The moved binding carries the in-force fact it would have had: excluded means not in force.
        var lapsed = Kind is AuthorizationCounterfactualKind.ValidityLapse
            ? deciding with { ValidUntil = evidence.At, InForce = false }
            : deciding with { InForce = false };
        return evidence.WithBindings(
            remaining,
            [.. evidence.Excluded, new AuthorizationExcludedBinding(lapsed, reason)]);
    }

    private AuthorizationDecisionEvidence ApplyTowardAllow(AuthorizationDecisionEvidence evidence)
    {
        if (Kind is AuthorizationCounterfactualKind.BindingNarrowing)
        {
            var narrowed = evidence.Bindings[BindingOrdinal];
            return evidence.WithBindings(
                [
                    .. evidence.Bindings.Where((_, index) => index != BindingOrdinal),
                    narrowed with
                    {
                        Atom = new PermissionAtom(narrowed.Atom.Operation, evidence.ActAtom.Scope),
                        GrantScope = evidence.ActAtom.Scope,
                    },
                ],
                evidence.Excluded);
        }

        var excluded = evidence.Excluded[BindingOrdinal];
        var restored = Kind is AuthorizationCounterfactualKind.ValidityLapse
            ? excluded.Binding with { ValidUntil = null, InForce = true }
            : excluded.Binding with { InForce = true };
        return evidence.WithBindings(
            [.. evidence.Bindings, restored],
            [.. evidence.Excluded.Where((_, index) => index != BindingOrdinal)]);
    }

    private static int Ordinal(
        IReadOnlyList<AuthorizationExcludedBinding> excluded,
        AuthorizationExclusionReason reason,
        PermissionAtom act)
    {
        for (var index = 0; index < excluded.Count; index++)
        {
            if (excluded[index].Reason == reason && excluded[index].Binding.Atom.Covers(act))
                return index;
        }

        return -1;
    }

    private static string Name(AuthorizationAtomDerivation binding) =>
        $"{binding.GrantId}@{binding.GrantOwnerVersion}";
}
