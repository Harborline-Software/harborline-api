using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Harborline.Api.Foundation.PasswordHashing.DependencyInjection;

/// <summary>
/// Write-path floor enforcement startup assertion (ADR 0097 S3 sec-eng substrate
/// amendment). Captures the bound <see cref="Argon2idHashOptions"/> snapshot at
/// composition-root build time and, at
/// <see cref="IHostedService.StartAsync(System.Threading.CancellationToken)"/>, throws
/// <see cref="Argon2idBelowFloorException"/> when any parameter falls below its
/// substrate-tier floor (§"Cryptographic floor requirements"). Per generic-host behavior
/// on hosted-service startup failure, the host's <c>RunAsync</c> throws and the process
/// exits non-zero before serving the first request — making the substrate's
/// non-substitutable-downward property substrate-tier-enforced rather than documentation
/// discipline.
/// </summary>
/// <remarks>
/// Companion to <see cref="Argon2idHashOptionsValidator"/> (the
/// <c>IValidateOptions&lt;T&gt;</c> mechanism fires at first <c>IOptions&lt;T&gt;</c>
/// resolution, which may be lazy; this <c>IHostedService</c> fires unconditionally at
/// host startup). Both are defense-in-depth against the same parameter-downgrade foot-gun.
/// Registered exactly once via
/// <see cref="PasswordHashingServiceCollectionExtensions.AddHarborlinePasswordHashingSubstrate"/>.
/// </remarks>
public sealed class Argon2idParameterFloorAssertion : IHostedService
{
    private readonly Argon2idHashOptions _options;

    /// <summary>
    /// Constructs the assertion, capturing the bound options snapshot.
    /// </summary>
    public Argon2idParameterFloorAssertion(IOptions<Argon2idHashOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var violation = Argon2idFloors.FirstViolation(_options);
        if (violation is { } v)
        {
            throw new Argon2idBelowFloorException(v.ParameterName, v.ExpectedFloor, v.ActualValue);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
