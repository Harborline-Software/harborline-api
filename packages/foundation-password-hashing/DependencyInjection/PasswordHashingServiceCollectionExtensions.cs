using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Harborline.Api.Foundation.PasswordHashing.DependencyInjection;

/// <summary>
/// Composition-root DI extensions for the ADR 0097 Tier-1 PasswordHasher substrate.
/// Three helpers, by design (mirrors ADR 0096's
/// <c>AddHarborlineVendorProviderSubstrate</c> / <c>AddHarborlineVendorProvider</c> separation):
/// </summary>
/// <list type="bullet">
///   <item><description>
///     <see cref="AddHarborlinePasswordHashingSubstrate"/> — substrate-init helper invoked
///     exactly once; registers the two startup <c>IHostedService</c> assertions.
///   </description></item>
///   <item><description>
///     <see cref="AddHarborlinePasswordHashing{TUser}"/> — registers the real Argon2id
///     concrete + the options + the options validator.
///   </description></item>
///   <item><description>
///     <see cref="AddHarborlineMockPasswordHashing{TUser}"/> — registers the constant-string
///     mock concrete (compile-time-constrained to marker-carrying types).
///   </description></item>
/// </list>
public static class PasswordHashingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the substrate startup assertions (A6 LOAD-BEARING): the
    /// <see cref="MockPasswordHasherProductionGuardAssertion"/> and the
    /// <see cref="Argon2idParameterFloorAssertion"/>, each as an
    /// <c>IHostedService</c>. Call once at composition-root assembly time. Both
    /// registrations use <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IHostedService&gt;(...))</c>
    /// with the factory-with-capture form so the production guard receives the captured
    /// <see cref="IServiceCollection"/> reference and so repeated substrate-init calls (or
    /// multiple substrate packages) yield exactly one of each assertion rather than
    /// duplicates.
    /// </summary>
    /// <remarks>
    /// <c>TryAddEnumerable</c> deduplicates on the <c>(IHostedService, TImplementation)</c>
    /// pair — the correct dedup semantic for hosted-service registrations. A plain
    /// <c>TryAddSingleton&lt;IHostedService, T&gt;()</c> would deduplicate on the
    /// <c>IHostedService</c> service type alone and silently drop the second of the two
    /// distinct assertions.
    /// </remarks>
    public static IServiceCollection AddHarborlinePasswordHashingSubstrate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The typed (TService, TImplementation) factory overload keeps the implementation
        // type distinguishable so TryAddEnumerable's (IHostedService, TImplementation)-pair
        // dedup works — the bare Singleton<IHostedService>(factory) form yields an
        // implementation type of IHostedService itself, which TryAddEnumerable rejects.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, MockPasswordHasherProductionGuardAssertion>(
                _ => new MockPasswordHasherProductionGuardAssertion(services)));

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, Argon2idParameterFloorAssertion>());

        return services;
    }

    /// <summary>
    /// Registers <see cref="Argon2idPasswordHasher{TUser}"/> as the
    /// <c>IPasswordHasher&lt;TUser&gt;</c> concrete (A4 LOAD-BEARING — via
    /// <c>services.Replace</c>, displacing any prior registration idempotently so the
    /// Step 2 composition-root cutover is correctness-stable regardless of deletion order).
    /// Binds <see cref="Argon2idHashOptions"/> (applying <paramref name="configure"/> when
    /// non-null) and registers <see cref="Argon2idHashOptionsValidator"/> via
    /// <c>TryAddEnumerable</c> so a below-floor configuration fails fast.
    /// </summary>
    /// <typeparam name="TUser">The user entity type.</typeparam>
    /// <param name="services">DI container.</param>
    /// <param name="configure">Optional Argon2id parameter override. When null, the
    /// OWASP-minimum defaults apply.</param>
    /// <param name="lifetime">Registration lifetime (C1). Default
    /// <see cref="ServiceLifetime.Singleton"/> — the hasher is stateless per request (the
    /// salt is generated per <c>HashPassword</c> call from the BCL RNG).</param>
    public static IServiceCollection AddHarborlinePasswordHashing<TUser>(
        this IServiceCollection services,
        Action<Argon2idHashOptions>? configure = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TUser : class
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the IOptions<Argon2idHashOptions> infrastructure (OptionsManager etc.)
        // so IOptions<Argon2idHashOptions> resolves with substrate defaults even when no
        // configure delegate is supplied.
        services.AddOptions<Argon2idHashOptions>();

        if (configure is not null)
        {
            // Register the configure delegate via Microsoft.Extensions.Options primitives
            // only — the `services.Configure<T>(Action<T>)` convenience overload pulls in a
            // compile-time reference to Microsoft.Extensions.Configuration.Abstractions
            // (IConfiguration), which this substrate deliberately does NOT reference
            // (ADR 0097 Q7 — configuration binding is a composition-root concern).
            services.AddSingleton<IConfigureOptions<Argon2idHashOptions>>(
                new ConfigureNamedOptions<Argon2idHashOptions>(Options.DefaultName, configure));
        }

        // Defense-in-depth floor enforcement at the IOptions<T> validation layer (C3).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<Argon2idHashOptions>, Argon2idHashOptionsValidator>());

        services.Replace(ServiceDescriptor.Describe(
            typeof(IPasswordHasher<TUser>),
            typeof(Argon2idPasswordHasher<TUser>),
            lifetime));

        return services;
    }

    /// <summary>
    /// Registers <see cref="MockPasswordHasher{TUser}"/> as the
    /// <c>IPasswordHasher&lt;TUser&gt;</c> concrete (via <c>services.Replace</c> for the
    /// same idempotency reason as the real helper). The generic constraint requires a type
    /// implementing both <c>IPasswordHasher&lt;TUser&gt;</c> AND
    /// <see cref="IMockPasswordHasher"/> — only marker-carrying mock concretes can register
    /// via this helper, closing the mock-without-marker compile-time foot-gun.
    /// </summary>
    /// <remarks>
    /// The <c>IHostedService</c> production-guard assertions are NOT registered here —
    /// <see cref="AddHarborlinePasswordHashingSubstrate"/> is the canonical single point of
    /// registration for them (A6). Call <see cref="AddHarborlinePasswordHashingSubstrate"/>
    /// first (or rely on a composition-root convention that does so).
    /// </remarks>
    /// <typeparam name="TUser">The user entity type.</typeparam>
    public static IServiceCollection AddHarborlineMockPasswordHashing<TUser>(this IServiceCollection services)
        where TUser : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(
            ServiceDescriptor.Singleton<IPasswordHasher<TUser>, MockPasswordHasher<TUser>>());

        return services;
    }
}
