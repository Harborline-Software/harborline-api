using Harborline.Api.Foundation.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// Startup assertion: the volatile in-memory blob store must never be the resolved
/// <see cref="IBlobStore"/> in Production.
/// </summary>
/// <remarks>
/// <para>
/// This closes a defect the source repository already suffered once. `AddNodeForms` registers
/// <c>TryAddSingleton&lt;IBlobStore, NodeInMemoryBlobStore&gt;</c> as a convenience default, and
/// suppression relied entirely on a durable store being registered <em>first</em>. Card 3750 (gap
/// G2.1) records what happens when that ordering is not met: no shipped appsettings set a storage
/// role, so the bundled node ran the default Edge role, fell through to the in-memory default, and
/// blobs died with the process — never sealed at rest, with no error at any point.
/// </para>
/// <para>
/// Registration order is not a fail-closed control: it is satisfied by accident and broken by
/// accident, and its failure mode is silent data loss. This assertion makes the guarantee
/// order-independent, matching the destination repository's declared
/// <c>mockPolicy: development-and-test-only-fail-closed-in-production</c> and mirroring the
/// established shape of <c>MockPasswordHasherProductionGuardAssertion</c>.
/// </para>
/// <para>
/// Non-Production environments are unaffected — dev and test compose the volatile store
/// deliberately.
/// </para>
/// </remarks>
public sealed class VolatileBlobStoreProductionGuardAssertion : IHostedService
{
    private const string AspNetCoreEnvVar = "ASPNETCORE_ENVIRONMENT";
    private const string ProductionEnvironmentName = "Production";

    private readonly IServiceCollection _capturedServices;

    /// <summary>
    /// Constructs the assertion. <paramref name="capturedServices"/> MUST be the same
    /// <see cref="IServiceCollection"/> the composition root mutated, since the assertion
    /// inspects registered descriptors rather than resolving the service.
    /// </summary>
    public VolatileBlobStoreProductionGuardAssertion(IServiceCollection capturedServices)
    {
        _capturedServices = capturedServices ?? throw new ArgumentNullException(nameof(capturedServices));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var environment = Environment.GetEnvironmentVariable(AspNetCoreEnvVar);
        if (!string.Equals(environment, ProductionEnvironmentName, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // The LAST registration for a service type is the one the provider resolves, so inspect
        // the effective descriptor rather than any earlier one. A durable store registered after
        // the volatile default is correct and must not trip this guard.
        ServiceDescriptor? effective = null;
        foreach (var descriptor in _capturedServices)
        {
            if (descriptor.ServiceType == typeof(IBlobStore))
            {
                effective = descriptor;
            }
        }

        if (effective is null)
        {
            return Task.CompletedTask;
        }

        var concreteType = effective.ImplementationType ?? effective.ImplementationInstance?.GetType();
        if (concreteType is null)
        {
            // Factory-only registration cannot be discriminated without resolving, which would
            // instantiate the store during startup. Out of scope, matching the ADR 0096 discipline
            // the password-hashing guard follows.
            return Task.CompletedTask;
        }

        if (concreteType == typeof(NodeInMemoryBlobStore))
        {
            throw new InvalidOperationException(
                "The volatile in-memory blob store is not permitted in Production: it is the resolved "
                + "IBlobStore, so payloads would be lost on restart and never sealed at rest (see Card 3750, "
                + "gap G2.1). Register a durable FileSystemBlobStore wrapped by EnvelopeBlobStore before "
                + "calling AddNodeForms.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
