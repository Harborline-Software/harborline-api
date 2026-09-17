using System.Collections.Immutable;
using System.Diagnostics;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

public sealed class AuthorizationGate(
    IAuthorizationClosureSnapshotReader closure,
    IRecordStandingResolver standings,
    IAuthorizationDefinitionAtomReader definitions,
    IAuthorizationRosterConstraintReader rosterConstraints)
{
    private static readonly ActivitySource Decisions = new("Harborline.AuthorizationGate");

    private static readonly IReadOnlyDictionary<string, string> ResourceRecordKindExceptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["records"] = "record",
            ["ledger"] = "journal-entry",
        };

    internal AuthorizationGate(
        IAuthorizationClosureSnapshotReader closure,
        IRecordStandingResolver standings,
        IAuthorizationDefinitionAtomReader definitions)
        : this(closure, standings, definitions, TestMemberAuthorizationRosterConstraintReader.Shared)
    {
    }

    /// <summary>
    /// The install-root grant derivation the roster inputs used to carry: the principal's atoms whose scope
    /// is the install root, at the caller's instant. The gate reads its OWN closure; no caller holds a reader.
    /// </summary>
    public async ValueTask<PermissionSet> InstallRootPermissionsAsync(
        ActorId principal, TenantId tenant, DateTimeOffset at, CancellationToken ct = default)
    {
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:read@/"), principal, tenant,
            new AuthorizationTarget("tenant", tenant.Value, InstallWideScope), at);
        var snapshot = await closure.ReadAsync(request, ct).ConfigureAwait(false);
        return PermissionSet.From(snapshot.Derivations.Select(d => d.Atom)
            .Where(a => a.Scope.Value == "/")
            .Select(a => a.Operation.Value));
    }

    public async ValueTask<AuthorizationDecision> DecideAsync(
        AuthorizationGateRequest request,
        CancellationToken ct = default) =>
        await DecideCoreAsync(
            request, prospectiveAdministrator: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Decides membership admission, where the acting principal must have a verified live roster edge in
    /// addition to grant coverage. The dedicated entry point prevents caller-supplied roster switches from
    /// weakening or manufacturing this constraint.
    /// </summary>
    public async ValueTask<AuthorizationDecision> DecideMembershipAdmissionAsync(
        AuthorizationGateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Act.Operation.Value != TeamRolePermissions.MembersManage
            || request.Target.RecordKind != "members")
        {
            throw new ArgumentException(
                "Membership admission authority is valid only for members management acts.",
                nameof(request));
        }
        return await DecideCoreAsync(
            request, prospectiveAdministrator: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides the one transition where a verified non-member is about to receive Administrator.
    /// The dedicated entry point prevents a request flag from manufacturing prospective authority.
    /// </summary>
    public async ValueTask<AuthorizationDecision> DecideProspectiveAdministratorAsync(
        AuthorizationGateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Act.Operation.Value != TeamRolePermissions.MembersManage
            || request.Target.RecordKind != "members"
            || request.Target.RecordId != "handover")
            throw new ArgumentException(
                "Prospective Administrator authority is valid only for the members handover act.",
                nameof(request));
        return await DecideCoreAsync(
            request, prospectiveAdministrator: true, ct).ConfigureAwait(false);
    }

    private async ValueTask<AuthorizationDecision> DecideCoreAsync(
        AuthorizationGateRequest request,
        bool prospectiveAdministrator,
        CancellationToken ct)
    {
        using var activity = Decisions.StartActivity("decide");
        Validate(request);
        ct.ThrowIfCancellationRequested();

        var derivedRoster = await rosterConstraints
            .ReadAsync(request.Principal, request.Tenant, request.At, ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        derivedRoster ??= new AuthorizationRosterInputs(
            request.Principal.Value, Member: false, Ejected: true) { RegistryMember = false };
        var requireRosterMember = !prospectiveAdministrator
            && request.Act.Operation.Value == TeamRolePermissions.MembersManage
            && request.Target.RecordKind == "members";
        request = request with
        {
            // Caller-supplied roster facts and policy switches are never consulted. The gate records
            // the verified facts and its own fixed constraints on the immutable decision request.
            Roster = derivedRoster with
            {
                ProspectiveAdministratorGrant = prospectiveAdministrator,
                RequireMember = requireRosterMember,
                RequireGrantCoverage = requireRosterMember,
                RequiredPermissions = PermissionSet.Empty,
            },
            // A caller cannot manufacture a kernel denial code. Only the attenuation read below may set it.
            GrantRefusal = null,
        };

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
        if (requireRosterMember && effectiveRecordRoles.Contains(RoleReference.Administrator))
        {
            // An install-wide Administrator grant is the established non-roster administration path.
            // The gate derives this exception from its own closure; a caller cannot request it by
            // clearing RequireMember on the legacy roster observation.
            requireRosterMember = false;
            request = request with
            {
                Roster = request.Roster! with { RequireMember = false },
            };
        }
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

        if (request.Roster is { } roster)
        {
            var grantAllowed = atomCoverageAllowed;
            // Ticket 294 slice 2a — the prospective-Administrator rule. When the dedicated gate entry point
            // declares that the Administrator role is ABOUT to be conferred, the decision is made against the
            // atoms that role confers ONLY where the subject holds no roster edge. Where a roster edge
            // exists it is the authority the signed plane already published, so the decision is made
            // against the subject's OWN conferred grants and the prospective atoms are not added — a
            // successor the roster has narrowed is refused rather than handed a role that does nothing
            // (ticket 211 slice 3). An ejected subject is empty either way: ejection outranks a prospect.
            // Ticket 293 slice 4 — the roster no longer SUPPLIES a deciding set: the atoms are the ones
            // the gate derived from its own closure above, and this branch only constrains them.
            if (roster.Ejected)
            {
                atoms = [];
            }
            else if (prospectiveAdministrator && !roster.Member)
            {
                atoms = atoms
                    .Concat(PermissionSet.From(TeamRolePermissions.ForRole(TeamRole.Admin))
                        .Permissions.Select(permission => PermissionAtom.Parse($"{permission}@/")))
                    .Distinct()
                    .ToImmutableArray();
            }
            atomCoverageAllowed = atoms.Any(atom => atom.Covers(request.Act))
                && (!roster.RequireMember || roster.Member)
                && (!roster.RequireGrantCoverage || grantAllowed)
                && roster.RequiredPermissions.Permissions.All(permission =>
                    atoms.Any(atom => atom.Operation.Value == permission));
            namedRoleUnionAllowed = atomCoverageAllowed;
        }

        AuthorizationGrantAttenuationEvidence? attenuation = null;
        if (request.RequiredGrantAtoms is { } requiredAtoms)
        {
            var evaluated = ImmutableArray.CreateBuilder<AuthorizationGrantAtomEvidence>();
            foreach (var required in requiredAtoms.Atoms)
            {
                // The delegated atom can cover a different scope from the members:manage act. Read
                // that scope through the gate's own closure; a narrow grant cannot confer a root role.
                var requiredSnapshot = await closure.ReadAsync(request with
                {
                    Act = required,
                    Target = new AuthorizationTarget(RecordKindFor(required.Operation),
                        request.Target.RecordId, required.Scope),
                    RequiredGrantAtoms = null,
                }, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var covered = requiredSnapshot.Derivations.Any(item => item.Atom.Covers(required));
                evaluated.Add(new AuthorizationGrantAtomEvidence(required, covered,
                    requiredSnapshot.Derivations.OrderBy(DescribeAttenuationDerivation, StringComparer.Ordinal).ToImmutableArray(),
                    requiredSnapshot.Excluded.OrderBy(item => DescribeAttenuationDerivation(item.Binding), StringComparer.Ordinal)
                        .ThenBy(item => item.Reason).ToImmutableArray()));
                if (!covered)
                {
                    request = request with { GrantRefusal = request.GrantRefusal ?? "authorization.grant.attenuation_failed" };
                }
            }
            attenuation = new AuthorizationGrantAttenuationEvidence(evaluated.ToImmutable());
        }
        var verdict = atomCoverageAllowed && request.GrantRefusal is null ? AuthorizationVerdict.Allowed : AuthorizationVerdict.Denied;
        var verdictName = verdict.ToString().ToLowerInvariant();
        resolution.Add(new AuthorizationResolutionStep(
            AuthorizationResolutionStage.NamedRoleUnionVerdict,
            namedRoleUnion.Select(role => role.ToString()).Order(StringComparer.Ordinal)
                .Concat(namedRoleAtoms.Select(item => $"compare:{item.Role}:{item.Atom}->{request.Act}"))
                .Concat(request.RequiredGrantAtoms?.Atoms.Select(atom => $"grant-required:{atom}") ?? [])
                .ToArray(),
            [$"atom-coverage:{verdictName}", $"named-role-union:{verdictName}"]));

        ct.ThrowIfCancellationRequested();
        var decision = new AuthorizationDecision(
            request, verdict, atoms, derivations, standingSnapshot, resolution, snapshot.Excluded, attenuation);
        activity?.SetCustomProperty("authorization.evidence", decision.Evidence);
        return decision;
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
        if (request.Act.Operation.Value == Permission.CatalogueRead
            && request.Target.Scope.Value.Contains("/catalogue-fields/", StringComparison.Ordinal))
        {
            var field = CatalogueFieldTarget.Parse(request.Target.Scope.Value);
            if (field.Id != request.Target.RecordId || !request.Act.Scope.Equals(field.Scope))
                throw new ArgumentException("The requested catalogue field and target disagree.", nameof(request));
            return;
        }
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
        if (recordKind == "catalogue") return CatalogueFieldTarget.RecordScope(recordId);
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

    private static string DescribeAttenuationDerivation(AuthorizationAtomDerivation item) =>
        $"{DescribeDerivation(item)};scope:{item.GrantScope};in-force:{item.InForce}";

    private static string DescribeStanding(RecordStanding item) =>
        $"role:{item.Role};rule:{item.RuleId};evidence:{item.EvidenceVersion}";

    private static ImmutableHashSet<RoleReference> UnionStandingRoles(
        IReadOnlySet<RoleReference> effectiveRecordRoles,
        IReadOnlyList<RecordStanding> recordStandings) =>
        effectiveRecordRoles.Union(recordStandings.Select(item => item.Role)).ToImmutableHashSet();
}
