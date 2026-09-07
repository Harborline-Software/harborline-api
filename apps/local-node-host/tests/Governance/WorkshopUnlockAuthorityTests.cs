using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Governance;

/// <summary>
/// Tests for the node workshop-unlock authority (ADR 0144 AD.1, slice B5; ticket 205 slice 2): it resolves
/// <c>workshop:unlock</c> at its point of use through <see cref="AuthorizationGate"/>, from the
/// caller-supplied <see cref="AuthorizationWriteContext"/> — never from an ambient context. A principal whose
/// holdings cover the act is Granted; one whose holdings do not gets the accessible denial (reason +
/// remediation keys, never a bare bool).
/// </summary>
public sealed class WorkshopUnlockAuthorityTests
{
    private static readonly TenantId Tenant = new("tenant-workshop");

    [Fact]
    public async Task Principal_whose_holdings_cover_the_act_is_granted()
    {
        var authority = new NodeWorkshopUnlockAuthority(TestAuthorization.Gate(allowed: true));

        var decision = await authority.AuthorizeAsync(TestAuthorization.Write(Tenant));

        Assert.IsType<WorkshopUnlockDecision.Granted>(decision);
    }

    [Fact]
    public async Task Principal_without_workshop_unlock_is_denied_with_an_accessible_shape()
    {
        var authority = new NodeWorkshopUnlockAuthority(TestAuthorization.Gate(allowed: false));

        var decision = await authority.AuthorizeAsync(TestAuthorization.Write(Tenant));

        var denied = Assert.IsType<WorkshopUnlockDecision.Denied>(decision);
        // Never a bare denial — a reason + remediation the Harborline App can render (First-Aid denial UX).
        Assert.False(string.IsNullOrWhiteSpace(denied.ReasonCode));
        Assert.False(string.IsNullOrWhiteSpace(denied.ReasonKey));
        Assert.False(string.IsNullOrWhiteSpace(denied.RemediationKey));
        Assert.Equal(WorkshopUnlockDecision.MissingUnlockPermission, denied);
    }

    [Fact]
    public async Task The_act_reaching_the_gate_is_the_install_wide_workshop_unlock_request()
    {
        // The point-of-use half: the authority decides nothing itself — it hands the gate the caller's
        // principal/tenant/instant and the install-wide workshop:unlock act, with NO record target.
        AuthorizationGateRequest? seen = null;
        var authority = new NodeWorkshopUnlockAuthority(
            TestAuthorization.Gate(allowed: true, observed: request => seen = request));

        await authority.AuthorizeAsync(TestAuthorization.Write(Tenant, principal: "founder"));

        var request = Assert.IsType<AuthorizationGateRequest>(seen);
        Assert.Equal(WorkshopUnlock.Operation, request.Act.Operation);
        Assert.Equal("founder", request.Principal.Value);
        Assert.Equal(Tenant, request.Tenant);
        Assert.Equal(TestAuthorization.At, request.At);
        Assert.Equal(string.Empty, request.Target.RecordKind);
        Assert.Equal(string.Empty, request.Target.RecordId);
    }
}
