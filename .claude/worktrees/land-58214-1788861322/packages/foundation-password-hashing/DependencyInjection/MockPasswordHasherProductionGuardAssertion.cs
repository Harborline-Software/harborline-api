using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.OperationalEnvironment;

namespace Harborline.Api.Foundation.PasswordHashing.DependencyInjection;

/// <summary>
/// Production-safety startup assertion for the Tier-1 PasswordHasher substrate
/// (ADR 0097 D4c). Captures the <see cref="IServiceCollection"/> at composition-root
/// build time and, at
/// <see cref="IHostedService.StartAsync(System.Threading.CancellationToken)"/>, scans the
/// registration tree for any <c>IPasswordHasher&lt;TUser&gt;</c> concrete carrying the
/// <see cref="IMockPasswordHasher"/> marker. In <c>ASPNETCORE_ENVIRONMENT=Production</c>,
/// it throws <see cref="MockPasswordHasherInProductionException"/> unless the explicit
/// <c>HARBORLINE_ALLOW_MOCK_PASSWORD_HASHER</c> opt-out parses to <c>true</c> — failing the
/// host startup before the first signup request rather than silently bypassing password
/// hashing in production.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Closed-generic discrimination idiom (A3 LOAD-BEARING).</strong> The scan keys
/// off the descriptor's <see cref="ServiceDescriptor.ServiceType"/> via
/// <c>IsGenericType &amp;&amp; GetGenericTypeDefinition() == typeof(IPasswordHasher&lt;&gt;)</c>
/// — NOT <c>typeof(IPasswordHasher&lt;&gt;).IsAssignableFrom(...)</c>, which returns
/// <c>false</c> for open-generic-vs-closed-generic in .NET reflection. The marker check
/// (<c>typeof(IMockPasswordHasher).IsAssignableFrom(concreteType)</c>) is on the
/// implementation type — the correct assignability direction. This is the first fleet
/// instance of a closed-generic-over-open-generic <c>ServiceDescriptor</c> scan; ADR 0096's
/// non-generic <c>MockProviderProductionGuardAssertion</c> does not face this complication.
/// </para>
/// <para>
/// <strong>Why a <see cref="ServiceDescriptor"/> scan, not
/// <c>IServiceProvider.GetServices&lt;IMockPasswordHasher&gt;()</c>:</strong> marker-only
/// interfaces are not resolvable from the provider (the mocks register as
/// <c>IPasswordHasher&lt;TUser&gt;</c>, not as <see cref="IMockPasswordHasher"/>). The
/// descriptor scan is the only honest mechanism — mirrors the ADR 0091 R2 A1 / 0095 R2 A3 /
/// 0096 R2 precedent: assertions inspect the registration tree, not the runtime resolution
/// tree.
/// </para>
/// <para>
/// <strong>Factory-only registrations are out of scope</strong> (matches ADR 0096):
/// a factory registration has a null <see cref="ServiceDescriptor.ImplementationType"/> and
/// null <see cref="ServiceDescriptor.ImplementationInstance"/> at the StartAsync timestamp,
/// so the scan cannot see the concrete without resolving the factory (which it refuses to
/// do). Mocks MUST register via the type-based
/// <see cref="PasswordHashingServiceCollectionExtensions.AddHarborlineMockPasswordHashing{TUser}"/>
/// helper so the scan sees them.
/// </para>
/// </remarks>
public sealed class MockPasswordHasherProductionGuardAssertion : IHostedService
{
    private const string OptOutEnvVar = "HARBORLINE_ALLOW_MOCK_PASSWORD_HASHER";
    private const string AspNetCoreEnvVar = "ASPNETCORE_ENVIRONMENT";
    private const string ProductionEnvironmentName = "Production";

    private readonly IServiceCollection _capturedServices;

    /// <summary>
    /// Constructs the assertion. <paramref name="capturedServices"/> MUST be the same
    /// <see cref="IServiceCollection"/> instance the composition root mutated during DI
    /// registration; the assertion closes over the reference. The descriptor list is
    /// effectively frozen post-Build per Microsoft.Extensions.DependencyInjection
    /// convention (C4).
    /// </summary>
    public MockPasswordHasherProductionGuardAssertion(IServiceCollection capturedServices)
    {
        _capturedServices = capturedServices ?? throw new ArgumentNullException(nameof(capturedServices));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var optOutRaw = HarborlineOperationalEnvironment.Read(OptOutEnvVar);

        // Bypass in non-Production environments — dev/test/staging may run with mocks.
        var environment = Environment.GetEnvironmentVariable(AspNetCoreEnvVar);
        if (!string.Equals(environment, ProductionEnvironmentName, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // Bypass when the explicit opt-out parses canonically to true.
        // bool.TryParse handles "true"/"True"/"TRUE"/"false"; non-parseable values
        // ("1", "yes", "on") are treated as false — fail-closed.
        if (bool.TryParse(optOutRaw, out var optOut) && optOut)
        {
            return Task.CompletedTask;
        }

        foreach (var descriptor in _capturedServices)
        {
            // Closed-generic discrimination on the ServiceType (A3 LOAD-BEARING).
            if (!descriptor.ServiceType.IsGenericType)
            {
                continue;
            }

            if (descriptor.ServiceType.GetGenericTypeDefinition() != typeof(IPasswordHasher<>))
            {
                continue;
            }

            var concreteType = descriptor.ImplementationType
                ?? descriptor.ImplementationInstance?.GetType();
            if (concreteType is null)
            {
                // Factory-only registration — out of scope (matches ADR 0096 discipline).
                continue;
            }

            // Marker check on the implementation type — the correct assignability direction.
            if (!typeof(IMockPasswordHasher).IsAssignableFrom(concreteType))
            {
                continue;
            }

            throw new MockPasswordHasherInProductionException(descriptor.ServiceType, concreteType);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
