using System.Collections.Immutable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

public sealed class AuthorizationGate(
    IAuthorizationClosureSnapshotReader closure,
    IRecordStandingResolver standings,
    IAuthorizationDefinitionAtomReader definitions)
{
    private static readonly IReadOnlyDictionary<string, string> ResourceRecordKindExceptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["records"] = "record",
            ["ledger"] = "journal-entry",
        };

    public async ValueTask<AuthorizationDecision> DecideAsync(
        AuthorizationGateRequest request,
        CancellationToken ct = default)
    {
        Validate(request);
        ct.ThrowIfCancellationRequested();

        var resolution = new List<AuthorizationResolutionStep>(4)
        {
            new(
                AuthorizationResolutionStage.ActKind,
                [$"act:{request.Act}", $"target:{request.Target.RecordKind}/{request.Target.RecordId}@{request.Target.Scope}"],
                [$"operation:{request.Act.Operation}", $"scope:{request.Act.Scope}"]),
        };

        var snapshot = await closure.ReadAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var derivations = snapshot.Derivations.ToImmutableArray();
        var effectiveRecordRoles = derivations.Select(item => item.Role).ToImmutableHashSet();
        resolution.Add(new AuthorizationResolutionStep(
            AuthorizationResolutionStage.EffectiveRecordRoles,
            derivations.Select(DescribeDerivation).Order(StringComparer.Ordinal).ToArray(),
            effectiveRecordRoles.Select(role => role.ToString()).Order(StringComparer.Ordinal).ToArray()));

        var resolvedStandings = await standings.ResolveAsync(request, effectiveRecordRoles, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var standingSnapshot = resolvedStandings.ToImmutableArray();
        resolution.Add(new AuthorizationResolutionStep(
            AuthorizationResolutionStage.RecordStandings,
            effectiveRecordRoles.Select(role => role.ToString()).Order(StringComparer.Ordinal).ToArray(),
            standingSnapshot.Select(DescribeStanding).Order(StringComparer.Ordinal).ToArray()));

        var namedRoleUnion = UnionStandingRoles(effectiveRecordRoles, standingSnapshot);
        var namedRoleAtoms = await ReadNamedRoleAtomsAsync(
            request, derivations, effectiveRecordRoles, standingSnapshot, definitions, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var standingRoles = standingSnapshot.Select(item => item.Role).ToImmutableHashSet();
        var atoms = derivations.Select(item => item.Atom)
            .Concat(namedRoleAtoms.Where(item => standingRoles.Contains(item.Role)).Select(item => item.Atom))
            .Distinct()
            .ToImmutableArray();
        var atomCoverageAllowed = atoms.Any(atom => atom.Covers(request.Act));
        var namedRoleUnionAllowed = namedRoleAtoms.Any(item => item.Atom.Covers(request.Act));
        if (atomCoverageAllowed != namedRoleUnionAllowed)
            throw new InvalidOperationException("Authorization atom and named-role readings diverged.");

        var verdict = atomCoverageAllowed ? AuthorizationVerdict.Allowed : AuthorizationVerdict.Denied;
        var verdictName = verdict.ToString().ToLowerInvariant();
        resolution.Add(new AuthorizationResolutionStep(
            AuthorizationResolutionStage.NamedRoleUnionVerdict,
            namedRoleUnion.Select(role => role.ToString()).Order(StringComparer.Ordinal)
                .Concat(namedRoleAtoms.Select(item => $"compare:{item.Role}:{item.Atom}->{request.Act}"))
                .ToArray(),
            [$"atom-coverage:{verdictName}", $"named-role-union:{verdictName}"]));

        ct.ThrowIfCancellationRequested();
        return new AuthorizationDecision(
            request, verdict, atoms, derivations, standingSnapshot, resolution, snapshot.Excluded);
    }

    private static void Validate(AuthorizationGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Principal.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Tenant.Value);
        ArgumentNullException.ThrowIfNull(request.Target.Scope);

        // Ledger L600/L671 — a request may omit its record target ONLY for an operation the definition side
        // declares install-wide; every other operation refuses, naming itself. An install-wide operation that
        // DOES carry a record target falls through to the record checks below and is validated like any other.
        if (string.IsNullOrWhiteSpace(request.Target.RecordKind)
            && string.IsNullOrWhiteSpace(request.Target.RecordId))
        {
            if (!PermissionVocabulary.IsInstallWide(request.Act.Operation))
            {
                throw new ArgumentException(
                    $"Operation '{request.Act.Operation.Value}' requires a record target; only an operation "
                    + "declared install-wide may omit record scope.",
                    nameof(request));
            }

            if (!request.Target.Scope.Equals(InstallWideScope) || !request.Act.Scope.Equals(InstallWideScope))
            {
                throw new ArgumentException(
                    $"Install-wide operation '{request.Act.Operation.Value}' must use the install root scope.",
                    nameof(request));
            }

            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Target.RecordKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Target.RecordId);
        var resource = request.Act.Operation.Value.Split(':', 2)[0];
        var expectedRecordKind = RecordKindFor(request.Act.Operation);
        var exactPackTarget = string.Equals(resource, "packages", StringComparison.Ordinal)
            && string.Equals(request.Target.RecordKind, "pack", StringComparison.Ordinal);
        if (!exactPackTarget
            && !string.Equals(request.Target.RecordKind, expectedRecordKind, StringComparison.Ordinal))
            throw new ArgumentException("The requested operation does not belong to the target record kind.", nameof(request));
        var canonicalScope = CanonicalTargetScope(request.Tenant, request.Target.RecordKind, request.Target.RecordId);
        if (!request.Target.Scope.Equals(canonicalScope) || !request.Act.Scope.Equals(canonicalScope))
            throw new ArgumentException("The requested act and target must use the canonical target scope.", nameof(request));
    }

    /// <summary>
    /// The record kind a record-scoped act on <paramref name="operation"/> must target — the operation's
    /// resource, with the two catalogue exceptions. This is the ONE reading of "which record kind does this
    /// operation address": <c>Validate</c> enforces it, and a point-of-use caller derives its target kind
    /// from it rather than spelling a second copy at the call site (ticket 205).
    /// </summary>
    public static string RecordKindFor(AuthorizationOperation operation)
    {
        var resource = operation.Value.Split(':', 2)[0];
        return ResourceRecordKindExceptions.GetValueOrDefault(resource, resource);
    }

    /// <summary>The install root — the one fixed scope an install-wide act resolves against, mirroring the
    /// scope the founding definitions already carry (ledger L600/L671).</summary>
    internal static ScopeExpression InstallWideScope { get; } = ScopeExpression.Parse("/");

    internal static ScopeExpression CanonicalTargetScope(TenantId tenant, string recordKind, string recordId)
    {
        if (string.Equals(recordKind, "tenant", StringComparison.Ordinal))
        {
            if (!string.Equals(recordId, tenant.Value, StringComparison.Ordinal))
                throw new ArgumentException("A tenant target id must identify the request tenant.", nameof(recordId));
            return ScopeExpression.Parse("/");
        }

        if (recordId.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException("A record id cannot contain a scope separator.", nameof(recordId));
        return ScopeExpression.Parse($"/records/{recordId}");
    }

    private static async ValueTask<ImmutableArray<NamedRoleAtom>> ReadNamedRoleAtomsAsync(
        AuthorizationGateRequest request,
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        IReadOnlySet<RoleReference> effectiveRecordRoles,
        IReadOnlyList<RecordStanding> recordStandings,
        IAuthorizationDefinitionAtomReader definitions,
        CancellationToken ct)
    {
        var result = ImmutableArray.CreateBuilder<NamedRoleAtom>();
        var standingRoles = recordStandings.Select(item => item.Role).ToImmutableHashSet();
        foreach (var role in UnionStandingRoles(effectiveRecordRoles, recordStandings))
        {
            var definitionAtoms = await definitions.AtomsForRoleAsync(request.Tenant, role, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var grantScopes = derivations.Where(item => item.Role == role).Select(item => item.GrantScope).Distinct().ToArray();
            if (standingRoles.Contains(role))
            {
                foreach (var definitionAtom in definitionAtoms)
                {
                    var scoped = definitionAtom.Scope.Intersect(request.Target.Scope);
                    if (scoped is not null)
                        result.Add(new NamedRoleAtom(role, new PermissionAtom(definitionAtom.Operation, scoped)));
                }
            }

            foreach (var definitionAtom in definitionAtoms)
            foreach (var grantScope in grantScopes)
            {
                var scoped = definitionAtom.Scope.Intersect(grantScope);
                if (scoped is not null && scoped.Contains(request.Target.Scope))
                    result.Add(new NamedRoleAtom(role, new PermissionAtom(definitionAtom.Operation, scoped)));
            }
        }
        return result.Distinct().ToImmutableArray();
    }

    private sealed record NamedRoleAtom(RoleReference Role, PermissionAtom Atom);

    private static string DescribeDerivation(AuthorizationAtomDerivation item) =>
        $"role:{item.Role};atom:{item.Atom};grant:{item.GrantId}@{item.GrantOwnerVersion};definition:{item.DefinitionId};valid:{item.ValidFrom:O}..{item.ValidUntil:O}";

    private static string DescribeStanding(RecordStanding item) =>
        $"role:{item.Role};rule:{item.RuleId};evidence:{item.EvidenceVersion}";

    private static ImmutableHashSet<RoleReference> UnionStandingRoles(
        IReadOnlySet<RoleReference> effectiveRecordRoles,
        IReadOnlyList<RecordStanding> recordStandings) =>
        effectiveRecordRoles.Union(recordStandings.Select(item => item.Role)).ToImmutableHashSet();
}
