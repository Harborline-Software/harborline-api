using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Wire-stability guard: the centralized identity audit event-type constants bind to persisted
/// hash-chain history and replay checks, so their values must never change.
/// </summary>
[Trait("PlanCard", "MTW-2-2621")]
public sealed class InstallationIdentityAuditEventTypesTests
{
    [Theory]
    [InlineData(
        InstallationIdentityAuditEventTypes.FounderBootstrapped,
        "InstallationFounderBootstrapped")]
    [InlineData(
        InstallationIdentityAuditEventTypes.SessionEstablished,
        "WebTenantSelected")]
    [InlineData(
        InstallationIdentityAuditEventTypes.SessionRevoked,
        "WebUserSessionRevoked")]
    [InlineData(
        InstallationIdentityAuditEventTypes.FounderBindingDesignated,
        "InstallationFounderBindingDesignated")]
    [InlineData(
        InstallationIdentityAuditEventTypes.InvitationIssued,
        "InstallationInvitationIssued")]
    [InlineData(
        InstallationIdentityAuditEventTypes.InvitationAccepted,
        "InstallationInvitationAccepted")]
    public void Constant_Value_Is_Wire_Stable(string actual, string expected) =>
        Assert.Equal(expected, actual);
}
