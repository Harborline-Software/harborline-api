using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using System.Text.Json;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class RosterGateDecisionTests
{
    [Theory]
    [InlineData("/", "/", true)]
    [InlineData("/", "/records/one", true)]
    [InlineData("/records/one", "/", false)]
    [InlineData("/records/one", "/records/one", true)]
    [InlineData("/records/one", "/records/two", false)]
    public async Task Role_attenuation_checks_each_required_scope_inside_the_gate(
        string heldScope, string requiredScope, bool allowed)
    {
        var source = new ScopedGrantSource(heldScope);
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with
        {
            RequiredGrantAtoms = PermissionAtomSet.Of(PermissionAtom.Parse($"records:read@{requiredScope}")),
        };
        var decision = await gate.DecideAsync(request);
        Assert.Equal(allowed ? AuthorizationVerdict.Allowed : AuthorizationVerdict.Denied, decision.Verdict);
        Assert.Equal(allowed ? null : "authorization.grant.attenuation_failed", decision.Evidence.GrantRefusal);
        var attenuation = Assert.IsType<AuthorizationGrantAttenuationEvidence>(decision.GrantAttenuation);
        Assert.Same(attenuation, decision.Evidence.GrantAttenuation);
        var atom = Assert.Single(attenuation.Atoms);
        Assert.Equal(PermissionAtom.Parse($"records:read@{requiredScope}"), atom.Required);
        Assert.Equal(allowed, atom.Covered);
    }

    [Fact]
    public async Task Empty_role_attenuation_cannot_admit_an_actor_without_members_manage()
    {
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with
        { RequiredGrantAtoms = PermissionAtomSet.Empty };
        Assert.Equal(AuthorizationVerdict.Denied, (await TestAuthorization.Gate(false).DecideAsync(request)).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attenuation_evidence_retains_scoped_bindings_exclusions_and_the_recorded_result(bool revoked)
    {
        var source = new ScopedGrantSource("/records/one") { RevokeRead = revoked };
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with
        { RequiredGrantAtoms = PermissionAtomSet.Of(PermissionAtom.Parse("records:read@/records/one")) };
        var decision = await gate.DecideAsync(request);
        var atom = Assert.Single(decision.Evidence.GrantAttenuation!.Atoms);
        Assert.Equal(!revoked, atom.Covered);
        Assert.Equal(!revoked, decision.Evidence.Allowed);
        if (revoked)
        {
            var excluded = Assert.Single(atom.Excluded);
            Assert.Equal(AuthorizationExclusionReason.GrantRevoked, excluded.Reason);
            Assert.Equal("grant", excluded.Binding.GrantId);
            Assert.Equal(1, excluded.Binding.GrantOwnerVersion);
            Assert.Equal("definition", excluded.Binding.DefinitionId);
            Assert.Equal("/records/one", excluded.Binding.GrantScope.Value);
            Assert.False(excluded.Binding.InForce);
        }
        else
        {
            var binding = Assert.Single(atom.Bindings, item => item.Atom.Operation.Value == "records:read");
            Assert.Equal("grant", binding.GrantId);
            Assert.Equal(1, binding.GrantOwnerVersion);
            Assert.Equal("definition", binding.DefinitionId);
            Assert.Equal(request.At.AddDays(-1), binding.ValidFrom);
        }
        var facts = decision.Evidence.Project()[1].Facts;
        Assert.Contains("attenuation:required:records:read@/records/one;covered:" + (!revoked), facts);
        Assert.Contains(facts, fact => fact.Contains("attenuation:required:records:read@/records/one;", StringComparison.Ordinal)
            && fact.Contains("binding:grant@1", StringComparison.Ordinal));
        var frozen = JsonSerializer.Serialize(decision.Evidence.Project());
        Assert.Equal(frozen, JsonSerializer.Serialize((await gate.DecideAsync(request)).Evidence.Project()));
        source.RevokeRead = !revoked;
        Assert.NotEqual(frozen, JsonSerializer.Serialize((await gate.DecideAsync(request)).Evidence.Project()));
        Assert.Equal(frozen, JsonSerializer.Serialize(decision.Evidence.Project()));
        Assert.Equal(AuthorizationCounterfactualKind.None, AuthorizationCounterfactual.From(decision.Evidence).Kind);
    }

    [Fact]
    public async Task A_denied_requirement_does_not_discard_evidence_for_later_requirements()
    {
        var source = new ScopedGrantSource("/records/one");
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with
        { RequiredGrantAtoms = PermissionAtomSet.Of(PermissionAtom.Parse("records:read@/"), PermissionAtom.Parse("records:read@/records/one")) };
        var evidence = (await gate.DecideAsync(request)).Evidence;
        Assert.False(evidence.Allowed);
        Assert.Equal([false, true], evidence.GrantAttenuation!.Atoms.Select(atom => atom.Covered));
    }

    private sealed class ScopedGrantSource(string scope) : IAuthorizationClosureSnapshotReader, IAuthorizationDefinitionAtomReader
    {
        public bool RevokeRead { get; set; }
        private readonly PermissionAtom[] _atoms =
            [PermissionAtom.Parse("members:manage@/"), PermissionAtom.Parse($"records:read@{scope}")];

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(AuthorizationGateRequest request,
            CancellationToken ct = default) => ValueTask.FromResult(new AuthorizationClosureSnapshot(
                _atoms.Where(atom => atom.Scope.Contains(request.Target.Scope)
                    && (!RevokeRead || atom.Operation.Value != "records:read")).Select(atom =>
                    new AuthorizationAtomDerivation(atom, RoleReference.Administrator, "grant", 1, "definition",
                        atom.Scope, request.At.AddDays(-1), null, true)).ToArray(),
                RevokeRead ? [new AuthorizationExcludedBinding(new AuthorizationAtomDerivation(
                    _atoms[1], RoleReference.Administrator, "grant", 1, "definition", _atoms[1].Scope,
                    request.At.AddDays(-1), null, false), AuthorizationExclusionReason.GrantRevoked)] : []));

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(TenantId tenantId, RoleReference role,
            CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<PermissionAtom>>(_atoms);
    }

    [Fact]
    public async Task Omitted_caller_roster_is_refused_when_the_gate_derives_no_member()
    {
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite");

        var decision = await TestAuthorization.GateWithRoster(
            allowed: true,
            roster: null,
            derivedRole: AccessGrantAuthorizationSeed.MemberRole).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.False(decision.Evidence.Roster!.Member);
        Assert.True(decision.Evidence.Roster.Ejected);
        Assert.True(decision.Evidence.Roster.RequireMember);
    }

    [Fact]
    public async Task Caller_supplied_false_constraints_cannot_widen_the_gate_derived_roster()
    {
        var supplied = new AuthorizationRosterInputs("forged", Member: true, Ejected: false)
        {
            RequireMember = false,
            RequireGrantCoverage = false,
        };
        var derived = new AuthorizationRosterInputs("canonical-party", Member: false, Ejected: false);
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with
            { Roster = supplied };

        var decision = await TestAuthorization.GateWithRoster(
            allowed: true,
            roster: derived,
            derivedRole: AccessGrantAuthorizationSeed.MemberRole).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.Equal("canonical-party", decision.Evidence.Roster!.PartyId);
        Assert.False(decision.Evidence.Roster.Member);
        Assert.True(decision.Evidence.Roster.RequireMember);
        Assert.True(decision.Evidence.Roster.RequireGrantCoverage);
    }

    [Fact]
    public async Task Caller_supplied_deny_is_ignored_and_only_gate_derived_denials_reach_the_decision()
    {
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with
            { GrantRefusal = "caller.chosen" };

        var decision = await TestAuthorization.GateWithRoster(
            allowed: true, roster: new AuthorizationRosterInputs("party", Member: true, Ejected: false))
            .DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Null(decision.Request.GrantRefusal);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, false)]
    public async Task Gate_derived_membership_ejection_and_grants_form_one_decision(
        bool member,
        bool ejected,
        bool holdsManage,
        bool allowed)
    {
        var roster = new AuthorizationRosterInputs("party", member, ejected);
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite");

        var decision = await TestAuthorization.GateWithRoster(
            holdsManage,
            roster,
            AccessGrantAuthorizationSeed.MemberRole).DecideAsync(request);

        Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
        Assert.Contains(decision.Evidence.Project()[1].Facts, fact => fact.Contains($"ejected:{ejected}"));
        Assert.Equal(AuthorizationCounterfactualKind.None, AuthorizationCounterfactual.From(decision.Evidence).Kind);
    }

    /// <summary>
    /// Ticket 294 slice 2a §1.4 — the prospective-Administrator rule, stated directly. A successor with NO
    /// roster edge is decided on the atoms the Administrator role is about to confer; a successor WITH a
    /// roster edge is decided on its own conferred grants, and the prospective atoms are not added. That
    /// single discriminator is what separates a handover to a stranger from a handover to a narrowed member.
    /// </summary>
    [Theory]
    // no roster edge, holds nothing → the prospective Administrator atoms decide → allowed.
    [InlineData(false, false, false, true)]
    // a roster edge that does not carry members:manage → own grants decide → refused.
    [InlineData(true, false, false, false)]
    // a roster edge that DOES carry members:manage → own grants decide → allowed.
    [InlineData(true, true, false, true)]
    // ejection outranks a prospect: no edge, but ejected → empty → refused.
    [InlineData(false, false, true, false)]
    public async Task A_prospective_successor_without_a_roster_edge_is_allowed_and_one_with_a_narrowed_edge_is_refused(
        bool member, bool holdsManage, bool ejected, bool allowed)
    {
        // Ticket 293 slice 4 — the roster no longer carries a permission set, so "holds members:manage"
        // is what the successor's OWN conferred grants derive at the gate's closure, not a caller input.
        var input = new AuthorizationRosterInputs("successor", member, ejected);
        var request = TestAuthorization.Write(new TenantId("tenant"), "successor")
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "handover");

        var decision = await TestAuthorization.GateWithRoster(holdsManage, input)
            .DecideProspectiveAdministratorAsync(request);

        Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
        Assert.Contains(
            decision.Evidence.Project()[1].Facts,
            fact => fact.Contains("prospective-administrator-grant:True"));
    }

    /// <summary>Without the flag the successor is decided on its own set alone — the flag is the whole rule.</summary>
    [Fact]
    public async Task Without_the_prospective_flag_a_successor_with_no_edge_and_no_grants_is_refused()
    {
        var input = new AuthorizationRosterInputs("successor", false, false);
        var request = TestAuthorization.Write(new TenantId("tenant"), "successor")
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "handover") with { Roster = input };

        var decision = await TestAuthorization.GateWithRoster(false, input).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
    }

    /// <summary>
    /// Ticket 293 slice 4 fix 4, item 7 — names the behaviour change the review found unpinned. The roster
    /// branch no longer PROJECTS its deciding set to the install root: a roster-carrying request at a narrower
    /// target scope is decided on the derivations the gate read for THAT scope. Before this slice the branch
    /// replaced the atoms with the caller's roster set re-parsed at <c>@/</c>, so a derivation that existed only
    /// at the record scope could not decide anything.
    /// </summary>
    [Fact]
    public async Task A_roster_carrying_request_at_a_narrower_scope_decides_on_that_scopes_derivations()
    {
        var input = new AuthorizationRosterInputs("party", true, false)
        {
            RequireMember = true, RequireGrantCoverage = true,
        };
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("records:read"), "record", "r1") with { Roster = input };

        var decision = await TestAuthorization.GateWithRoster(true, input).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        // The deciding derivation sits at the RECORD scope, not at the install root.
        Assert.Equal("/records/r1", Assert.Single(decision.Evidence.Bindings).Atom.Scope.Value);
        Assert.DoesNotContain(decision.Evidence.Bindings, binding => binding.Atom.Scope.Value == "/");
    }
}
