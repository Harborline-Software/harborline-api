using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.OperationalEnvironment;

namespace Harborline.Api.Foundation.Integrations.DependencyInjection;

/// <summary>
/// Composition-root DI extensions for ADR 0096 Tier-2 vendor-provider
/// substrate. Two helpers, by design:
/// </summary>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="AddHarborlineVendorProvider{TContract, TConcrete}(IServiceCollection, ServiceLifetime)"/>
///     — registers a mock concrete unconditionally. The generic constraint
///     <c>where TConcrete : class, TContract, IMockVendorProvider</c> makes
///     "the mock concrete carries the
///     <see cref="IMockVendorProvider"/> marker" a compile-error if
///     violated, not a runtime no-op.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="UseVendorProviderIfConfigured{TContract, TReal}(IServiceCollection, string)"/>
///     — conditionally swaps in a real adapter when its env-var-keyed
///     credential is present. Unconditionally records the
///     <c>(TContract → envVarKey)</c> mapping in
///     <see cref="IMockVendorEnvVarRegistry"/> so
///     <see cref="MockProviderProductionGuardAssertion"/> can enumerate
///     expected env-var keys in
///     <see cref="MockInProductionException"/>. The helper deliberately
///     reads env vars via
///     <see cref="Environment.GetEnvironmentVariable(string)"/> rather
///     than <c>IConfiguration</c> because <c>services.AddX</c> extension
///     methods run before
///     <see cref="ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(IServiceCollection)"/> —
///     no <c>IConfiguration</c> is resolvable at that point. Vendor secrets
///     consumed at request time (Postmark API key in the adapter's
///     <c>HttpClient</c> call) DO route through <c>IConfiguration</c> /
///     <c>IOptionsMonitor&lt;TOptions&gt;</c> per the standard ASP.NET Core
///     options pattern.
///     </description>
///   </item>
/// </list>
public static class VendorProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the substrate primitives consumed by ADR 0096 §D1c —
    /// <see cref="IMockVendorEnvVarRegistry"/> singleton and the
    /// <see cref="MockProviderProductionGuardAssertion"/>
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>. Call once
    /// at composition-root assembly time, BEFORE any
    /// <see cref="AddHarborlineVendorProvider{TContract, TConcrete}(IServiceCollection, ServiceLifetime)"/>
    /// or
    /// <see cref="UseVendorProviderIfConfigured{TContract, TReal}(IServiceCollection, string)"/>
    /// call.
    /// </summary>
    /// <remarks>
    /// The hosted-service registration closes over the
    /// <see cref="IServiceCollection"/> instance passed in — the assertion
    /// later iterates the captured descriptor list at
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync(System.Threading.CancellationToken)"/>.
    /// Per Microsoft.Extensions.DependencyInjection convention, the
    /// collection's descriptor list is effectively frozen post-Build, so the
    /// captured reference is sufficient.
    /// </remarks>
    public static IServiceCollection AddHarborlineVendorProviderSubstrate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registry = new MockVendorEnvVarRegistry();
        services.AddSingleton<IMockVendorEnvVarRegistry>(registry);
        services.AddHostedService(_ => new MockProviderProductionGuardAssertion(services, registry));
        return services;
    }

    /// <summary>
    /// Registers a mock Tier-2 vendor provider against its vendor-neutral
    /// contract. The generic constraint enforces marker membership at
    /// compile time: a non-marker concrete fails to compile here, not at
    /// runtime in the production-guard assertion.
    /// </summary>
    /// <typeparam name="TContract">The vendor-neutral contract (e.g.,
    /// <c>ICaptchaVerifier</c>).</typeparam>
    /// <typeparam name="TConcrete">The mock concrete type — must implement
    /// both <typeparamref name="TContract"/> AND
    /// <see cref="IMockVendorProvider"/>.</typeparam>
    /// <param name="services">DI container.</param>
    /// <param name="lifetime">Lifetime for the registration. Default
    /// <see cref="ServiceLifetime.Singleton"/> matches the canonical
    /// stateless-mock shape (<see cref="Captcha.InMemoryCaptchaVerifier"/>).</param>
    public static IServiceCollection AddHarborlineVendorProvider<TContract, TConcrete>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TContract : class
        where TConcrete : class, TContract, IMockVendorProvider
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Add(new ServiceDescriptor(typeof(TContract), typeof(TConcrete), lifetime));
        return services;
    }

    /// <summary>
    /// Conditionally swaps the mock registration for a real vendor adapter
    /// when the env var at <paramref name="envVarKey"/> resolves to a
    /// non-empty value. Unconditionally records the
    /// <c>(TContract → envVarKey)</c> mapping in
    /// <see cref="IMockVendorEnvVarRegistry"/> so
    /// <see cref="MockProviderProductionGuardAssertion"/> can name the
    /// env-var in <see cref="MockInProductionException"/> when production
    /// composition resolved to the mock.
    /// </summary>
    /// <typeparam name="TContract">The vendor-neutral contract.</typeparam>
    /// <typeparam name="TReal">The real vendor adapter — MUST NOT implement
    /// <see cref="IMockVendorProvider"/> (the asymmetry is by design;
    /// marker membership is the canonical mock-vs-real discriminator).
    /// </typeparam>
    /// <param name="services">DI container.</param>
    /// <param name="envVarKey">The env-var name whose presence gates the
    /// swap (e.g., <c>"POSTMARK_API_KEY"</c>,
    /// <c>"TURNSTILE_SECRET_KEY"</c>). Empty-string env vars are treated as
    /// absent — closing the <c>POSTMARK_API_KEY=""</c> foot-gun.</param>
    /// <remarks>
    /// <para>
    /// Lifetime preservation per ADR 0096 §D4 amendment (Option α): the
    /// swap inherits the prior <see cref="ServiceDescriptor.Lifetime"/> so
    /// a <see cref="ServiceLifetime.Singleton"/>-registered mock is
    /// replaced by a <see cref="ServiceLifetime.Singleton"/>-registered
    /// real adapter. Scoped and Transient pass through likewise. If no
    /// prior descriptor exists, the swap registers as
    /// <see cref="ServiceLifetime.Singleton"/> (the canonical default
    /// matching <see cref="AddHarborlineVendorProvider{TContract, TConcrete}(IServiceCollection, ServiceLifetime)"/>).
    /// </para>
    /// <para>
    /// The env-var-direct-read (vs <c>IConfiguration</c>) trade is
    /// deliberate: <c>services.AddX</c> extension methods run before
    /// <see cref="ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(IServiceCollection)"/>,
    /// so <c>IConfiguration</c> is not resolvable at this point. The
    /// direct env-var read also avoids coupling to config-binding order.
    /// Vendor secrets consumed at request time (the adapter's
    /// <c>HttpClient</c> call) DO route through <c>IConfiguration</c> /
    /// <c>IOptionsMonitor&lt;TOptions&gt;</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection UseVendorProviderIfConfigured<TContract, TReal>(
        this IServiceCollection services,
        string envVarKey)
        where TContract : class
        where TReal : class, TContract
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(envVarKey);

        // Unconditionally record the (TContract → envVarKey) mapping so the
        // production-guard assertion can enumerate expected env-var keys in
        // MockInProductionException. This happens regardless of whether the
        // swap fires in THIS deployment.
        var registry = ResolveRegistryDescriptor(services);
        registry.Register(typeof(TContract), envVarKey);

        var envValue = HarborlineOperationalEnvironment.Read(envVarKey);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            // Real-adapter env var absent — leave the mock registration
            // intact. Production-guard assertion will catch this at startup
            // if ASPNETCORE_ENVIRONMENT=Production and the global opt-out
            // is not set.
            return services;
        }

        // Inherit the prior descriptor's lifetime per Option α (ADR 0096
        // §D4 amendment). If no prior descriptor exists (caller forgot to
        // call AddHarborlineVendorProvider first), default to Singleton —
        // matching the canonical mock-default lifetime.
        var priorLifetime = ServiceLifetime.Singleton;
        var priorDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TContract));
        if (priorDescriptor is not null)
        {
            priorLifetime = priorDescriptor.Lifetime;
        }

        services.Replace(new ServiceDescriptor(typeof(TContract), typeof(TReal), priorLifetime));
        return services;
    }

    /// <summary>
    /// Resolves the <see cref="IMockVendorEnvVarRegistry"/> singleton
    /// instance from the service collection. The substrate-init helper
    /// <see cref="AddHarborlineVendorProviderSubstrate(IServiceCollection)"/>
    /// registers it as a Singleton instance, so the descriptor's
    /// <see cref="ServiceDescriptor.ImplementationInstance"/> is non-null
    /// and we can read it back at composition-root call time without
    /// building the provider.
    /// </summary>
    private static IMockVendorEnvVarRegistry ResolveRegistryDescriptor(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMockVendorEnvVarRegistry));
        if (descriptor?.ImplementationInstance is IMockVendorEnvVarRegistry registry)
        {
            return registry;
        }

        throw new InvalidOperationException(
            $"{nameof(IMockVendorEnvVarRegistry)} is not registered as a singleton instance. "
            + $"Call {nameof(AddHarborlineVendorProviderSubstrate)} before any "
            + $"{nameof(AddHarborlineVendorProvider)} or "
            + $"{nameof(UseVendorProviderIfConfigured)} call at the composition root. "
            + "Per ADR 0096 §D1c.");
    }
}
