using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class TestRouteGateTests
{
    [Theory]
    [InlineData(true, AuthorizationVerdict.Allowed)]
    [InlineData(false, AuthorizationVerdict.Denied)]
    public async Task Overlapping_decisions_keep_their_own_grant_atoms(
        bool secondHoldsPermission, AuthorizationVerdict expectedSecond)
    {
        var source = new TestRouteGate.ScopedGrantSource(
            request => request.Principal.Value == "first" || secondHoldsPermission, []);
        var standings = new PausedFirstStandingResolver();
        var gate = new AuthorizationGate(source, standings, source);

        // A has read its granted closure, but has not read the matching named-role atoms.
        var first = gate.DecideAsync(Request("first", "records:write", "one")).AsTask();
        await standings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AuthorizationDecision second;
        try
        {
            // B completes an unrelated read while A is suspended. Its grant (or denial) must
            // neither replace A's atoms nor inherit A's permission.
            second = await gate.DecideAsync(Request("second", "records:read", "two"))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            standings.Release.TrySetResult();
        }
        var firstDecision = await first.WaitAsync(TimeSpan.FromSeconds(5));

        // Oracles: fixture-declared independent grants; these literals are not read from the gate.
        Assert.Equal(AuthorizationVerdict.Allowed, firstDecision.Verdict);
        Assert.Equal(expectedSecond, second.Verdict);
        Assert.Equal("records:write@/records/one", Assert.Single(firstDecision.AtomsConsidered).ToString());
        if (secondHoldsPermission)
            Assert.Equal("records:read@/records/two", Assert.Single(second.AtomsConsidered).ToString());
        else
            Assert.Empty(second.AtomsConsidered);
    }

    private static AuthorizationGateRequest Request(string principal, string operation, string recordId)
    {
        var scope = ScopeExpression.Parse($"/records/{recordId}");
        return new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(operation), scope),
            new ActorId(principal), TenantId.FromString("fixture-tenant"),
            new AuthorizationTarget("record", recordId, scope),
            AdmittedInstant.FromRecordedAct(DateTimeOffset.Parse("2026-10-07T00:00:00Z")));
    }

    private sealed class PausedFirstStandingResolver : IRecordStandingResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(
            AuthorizationGateRequest request, IReadOnlySet<RoleReference> roles,
            CancellationToken ct = default)
        {
            _ = roles;
            if (request.Principal.Value == "first")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return [];
        }
    }
}
