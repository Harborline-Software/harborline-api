using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>Creates the same durable founder genesis that the production ceremony creates.</summary>
internal static class InstallationIdentityTestBootstrap
{
    private static readonly string RootFingerprint = string.Join(":", Enumerable.Repeat("AB", 32));

    internal static async Task BootstrapAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identityFactory);

        var passwordHasher = new Argon2idPasswordHasher<object>(
            Options.Create(new Argon2idHashOptions()));
        var credentialHash = passwordHasher.HashPassword(
            new object(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var result = await new InstallationFounderBootstrapService(
                identityFactory,
                TimeProvider.System)
            .InitializeAsync(
                new InstallationFounderBootstrapCommand(
                    "test-founder",
                    credentialHash,
                    Guid.NewGuid().ToString("N"),
                    RootFingerprint,
                    $"test-installation-bootstrap-{Guid.NewGuid():N}"),
                cancellationToken);

        if (result.Status != InstallationFounderBootstrapStatus.Created)
        {
            throw new InvalidOperationException(
                $"Expected a fresh installation bootstrap, received {result.Status}.");
        }
    }
}
