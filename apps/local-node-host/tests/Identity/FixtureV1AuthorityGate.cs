using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// A settable <see cref="IInstallationIdentityV1AuthorityGate"/> for fixtures whose subject is not
/// the cutover itself.
/// </summary>
/// <remarks>
/// <para>
/// Default-admitting, which holds the installation at the pre-cutover
/// <c>LegacyV1Authoritative</c> posture every one of those fixtures assumes — the path under test
/// then runs exactly as it does before any migration begins.
/// </para>
/// <para>
/// <b>This double proves nothing about the cutover.</b> A fixture holding an always-admitting gate
/// cannot notice an accept path that was never wired to it. The wiring is proved by
/// <c>LegacyV1CutoverAuthorityProof</c>, which drives the REAL
/// <see cref="InstallationIdentityCutoverOrchestrator"/> over a file-backed store and asserts each
/// production accept path refuses after the marker commits. Do not add cutover assertions here;
/// add them there, where a mutation that unwires a path turns the proof red.
/// </para>
/// </remarks>
internal sealed class FixtureV1AuthorityGate(bool isAllowed = true)
    : IInstallationIdentityV1AuthorityGate
{
    /// <summary>A shared always-admitting instance for fixtures that never flip the stage.</summary>
    internal static FixtureV1AuthorityGate Admitting => new();

    /// <summary>Whether the gate currently admits; settable so a test can commit the cutover.</summary>
    internal bool IsAllowed { get; set; } = isAllowed;

    public Task<InstallationIdentityV1MutationAdmission> CheckV1MutationAdmissionAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Admission());

    public Task<InstallationIdentityV1MutationAdmission> CheckLegacyBearerAdmissionAsync(
        InstallationIdentityLegacyBearerAudience audience,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Admission());

    private InstallationIdentityV1MutationAdmission Admission() =>
        IsAllowed
            ? new InstallationIdentityV1MutationAdmission(true, null)
            : new InstallationIdentityV1MutationAdmission(
                false,
                InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal);
}
