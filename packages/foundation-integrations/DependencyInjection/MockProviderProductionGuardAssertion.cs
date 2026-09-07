using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.OperationalEnvironment;

namespace Harborline.Api.Foundation.Integrations.DependencyInjection;

/// <summary>
/// Startup assertion per ADR 0096 §D1c. Fails closed at
/// <see cref="IHostedService.StartAsync(System.Threading.CancellationToken)"/>
/// when production composition resolves to a mock Tier-2 vendor without an
/// explicit opt-out — the canonical mock-first production-safety substrate
/// invariant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mechanism (Path α — registration-snapshot scan):</strong> the
/// constructor captures the <see cref="IServiceCollection"/> at composition-
/// root build time (the caller passes the same instance that
/// <see cref="ServiceCollectionServiceExtensions"/> mutated). At
/// <see cref="IHostedService.StartAsync(System.Threading.CancellationToken)"/>,
/// the assertion iterates the captured descriptors and, for each whose
/// <see cref="ServiceDescriptor.ImplementationType"/> or
/// <see cref="ServiceDescriptor.ImplementationInstance"/>'s runtime type
/// implements <see cref="IMockVendorProvider"/>, checks (a) whether the
/// per-contract real-adapter env var (sourced from
/// <see cref="IMockVendorEnvVarRegistry"/>) is present, OR (b) whether the
/// global opt-out env var <c>HARBORLINE_ALLOW_MOCK_PROVIDERS</c> parses to
/// <c>true</c> via <see cref="bool.TryParse(string, out bool)"/>. If
/// neither holds, the contract is added to a typed failure list and the
/// assertion throws <see cref="MockInProductionException"/>.
/// </para>
/// <para>
/// <strong>Why <see cref="ServiceDescriptor"/> scan, not
/// <see cref="ServiceProviderServiceExtensions.GetServices{T}(IServiceProvider)"/></strong>:
/// marker-only interfaces are not resolvable from <see cref="IServiceProvider"/>
/// unless the mock concretes are ALSO registered against
/// <see cref="IMockVendorProvider"/> directly (which they are not — they
/// are registered as <c>TContract</c>). The
/// <see cref="ServiceDescriptor.ImplementationType"/> scan is the only
/// honest mechanism for inspecting "which registered services carry the
/// marker." This mirrors the ADR 0095 R2 A3 precedent: assertions inspect
/// the <em>registration tree</em>, not the runtime resolution tree.
/// </para>
/// <para>
/// <strong>Startup ordering:</strong> this assertion has no co-assertion to
/// order against in this repository. ADR 0096 §"Substrate / layering notes"
/// prescribed "tenant-context coherence first, mock-provider production-safety
/// second", but the first of those — ADR 0095's
/// <c>BootstrapAndTenantMutualExclusionAssertion</c> — belonged to the
/// signal-bridge composition root, which is not carried across (see
/// harborline-migration docs/adr/0005). The two assertions were independent on
/// disjoint composition-root properties, so nothing about this one's
/// correctness depended on that ordering.
/// </para>
/// <para>
/// <strong>Factory-registered descriptors</strong> (those whose
/// <see cref="ServiceDescriptor.ImplementationType"/> is <c>null</c> and
/// <see cref="ServiceDescriptor.ImplementationInstance"/> is also <c>null</c>
/// — i.e., <c>ImplementationFactory</c>-only registrations) are out of
/// scope at this layer: per ADR 0096 §D1c, Tier-2 vendor adapters MUST
/// register via type through the constrained
/// <see cref="VendorProviderServiceCollectionExtensions.AddHarborlineVendorProvider{TContract, TConcrete}(IServiceCollection, ServiceLifetime)"/>
/// helper. Factory-only registrations cannot be inspected for marker
/// membership without resolving the factory, which the assertion
/// deliberately refuses to do (per ADR 0095 R2 A3 — assertions inspect the
/// registration tree, not the runtime resolution tree).
/// </para>
/// </remarks>
public sealed class MockProviderProductionGuardAssertion : IHostedService
{
    private const string OptOutEnvVar = "HARBORLINE_ALLOW_MOCK_PROVIDERS";
    private const string AspNetCoreEnvVar = "ASPNETCORE_ENVIRONMENT";
    private const string ProductionEnvironmentName = "Production";

    private readonly IServiceCollection _capturedServices;
    private readonly IMockVendorEnvVarRegistry _envVarRegistry;

    /// <summary>
    /// Constructs the assertion. The <paramref name="capturedServices"/>
    /// parameter MUST be the same <see cref="IServiceCollection"/> instance
    /// the composition root mutated during DI registration; the assertion
    /// closes over the reference. After
    /// <see cref="IServiceCollection"/> is consumed by
    /// <see cref="ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(IServiceCollection)"/>,
    /// the collection's descriptor list is effectively frozen per
    /// Microsoft.Extensions.DependencyInjection convention — the captured
    /// reference is sufficient.
    /// </summary>
    public MockProviderProductionGuardAssertion(
        IServiceCollection capturedServices,
        IMockVendorEnvVarRegistry envVarRegistry)
    {
        _capturedServices = capturedServices ?? throw new ArgumentNullException(nameof(capturedServices));
        _envVarRegistry = envVarRegistry ?? throw new ArgumentNullException(nameof(envVarRegistry));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var optOutRaw = HarborlineOperationalEnvironment.Read(OptOutEnvVar);

        // Bypass in non-Production environments — dev/test/staging may
        // legitimately run with mocks without opt-out.
        var aspNetCoreEnvironment = Environment.GetEnvironmentVariable(AspNetCoreEnvVar);
        if (!string.Equals(aspNetCoreEnvironment, ProductionEnvironmentName, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        // Bypass when the explicit opt-out env var parses canonically to true.
        // Per ADR 0096 §D1c amendment #2: bool.TryParse handles "true" / "True"
        // / "TRUE" / "false" per BCL; non-parseable values ("1", "yes", "on")
        // are treated as false — fail-closed.
        if (bool.TryParse(optOutRaw, out var optOut) && optOut)
        {
            return Task.CompletedTask;
        }

        var failures = new List<(Type ContractType, string EnvVarKey)>();

        foreach (var descriptor in _capturedServices)
        {
            var concreteType = ResolveConcreteType(descriptor);
            if (concreteType is null)
            {
                // Factory-only registration — out of scope per the mechanism
                // rationale. Vendor adapters MUST register via type through
                // the constrained AddHarborlineVendorProvider helper.
                continue;
            }

            if (!typeof(IMockVendorProvider).IsAssignableFrom(concreteType))
            {
                continue;
            }

            // The concrete is a mock. Determine the expected env-var key for
            // the operator's diagnostic. If the registry has no mapping for
            // this contract, the mock is freestanding (no real-adapter swap
            // path was registered at composition root) — we still fail-closed
            // in production but the diagnostic notes the missing mapping so
            // the operator can investigate.
            var envVarKey = _envVarRegistry.TryGet(descriptor.ServiceType, out var registryKey)
                ? registryKey
                : "(no env-var mapping recorded — call UseVendorProviderIfConfigured for this contract)";

            // Real-adapter env-var presence check. Empty string is treated as
            // absent (POSTMARK_API_KEY="" foot-gun closure per ADR 0096 §D1c
            // amendment #2). The typical case is: swap fired → mock
            // descriptor no longer present → we never reach this loop body
            // for that contract. If we DO reach this body with the env-var
            // present, the swap helper was either skipped or out-of-order;
            // production safety requires fail-closed regardless.
            if (_envVarRegistry.TryGet(descriptor.ServiceType, out _))
            {
                var realAdapterValue = HarborlineOperationalEnvironment.Read(envVarKey);
                if (!string.IsNullOrWhiteSpace(realAdapterValue))
                {
                    continue; // Env var IS present — swap was wired correctly elsewhere.
                }
            }

            failures.Add((descriptor.ServiceType, envVarKey));
        }

        if (failures.Count > 0)
        {
            throw new MockInProductionException(failures);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Resolves the concrete type backing a <see cref="ServiceDescriptor"/>:
    /// either <see cref="ServiceDescriptor.ImplementationType"/> (the common
    /// case for <c>AddX&lt;TContract, TConcrete&gt;</c>) or the runtime type
    /// of <see cref="ServiceDescriptor.ImplementationInstance"/> (the
    /// <c>AddSingleton(instance)</c> case). Factory-only registrations
    /// (<see cref="ServiceDescriptor.ImplementationFactory"/> set with no
    /// type) are deliberately out of scope per the assertion's mechanism
    /// rationale.
    /// </summary>
    private static Type? ResolveConcreteType(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is not null)
        {
            return descriptor.ImplementationType;
        }

        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance.GetType();
        }

        return null;
    }
}
