using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Test-only convenience for driving the two explicitly named coordinator continuations.</summary>
internal static class InstallationIdentityCoordinatorTestExtensions
{
    internal static Task<InstallationIdentityCoordinationResult> ExecuteAsync(
        this InstallationIdentityCoordinatorService coordinator,
        InstallationIdentityCoordinationCommand command,
        CancellationToken cancellationToken = default) =>
        coordinator.ExecuteAsync(
            command,
            InstallationIdentityCoordinatorContinuation.FounderAttachment,
            cancellationToken);

    internal static Task<InstallationIdentityCoordinationResult> ResumeAsync(
        this InstallationIdentityCoordinatorService coordinator,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        coordinator.ResumeAsync(
            correlationId,
            InstallationIdentityCoordinatorContinuation.Recovery,
            cancellationToken);
}
