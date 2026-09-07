using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal interface IInvitationAcceptanceGrantWriter
{
    Task<InitialGrantIssuanceResult> WriteAsync(
        AdmissionCompleted admission,
        string mintedAccountId,
        AuthorizationWriteContext authority,
        AuthorizationDecision decision,
        CancellationToken cancellationToken);
}

internal interface IInvitationAcceptanceMembershipWriter
{
    Task<InstallationIdentityCoordinationResult> WriteAsync(
        InstallationIdentityCoordinationCommand command,
        AuthorizationWriteContext authority,
        AuthorizationDecision decision,
        CancellationToken cancellationToken);
}
