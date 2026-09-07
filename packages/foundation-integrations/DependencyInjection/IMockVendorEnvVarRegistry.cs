namespace Harborline.Api.Foundation.Integrations.DependencyInjection;

/// <summary>
/// Singleton registry capturing the <c>(TContract → envVarKey)</c> mapping
/// for every Tier-2 contract for which
/// <see cref="VendorProviderServiceCollectionExtensions.UseVendorProviderIfConfigured{TContract, TReal}(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>
/// was called at composition-root construction time. Consumed by
/// <see cref="MockProviderProductionGuardAssertion"/> at
/// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync(System.Threading.CancellationToken)"/>
/// to enumerate expected env-var keys in
/// <see cref="MockInProductionException"/> when production composition
/// resolved to a mock.
/// </summary>
/// <remarks>
/// <para>
/// Without this registry, the production-guard assertion can name the mock
/// contract but cannot tell the operator <em>which env-var name they were
/// supposed to set</em>. Recording that key closes the silent-typo foot-gun
/// by returning the canonical name in the startup failure. This is the
/// load-bearing role per ADR 0096 §D1c.
/// </para>
/// <para>
/// Entries are written by <c>UseVendorProviderIfConfigured</c>
/// <strong>unconditionally</strong> — regardless of whether the conditional
/// swap fires in this deployment, the contract-to-env-var mapping is
/// recorded so the assertion can read it at startup. Reads happen
/// exactly once per process lifetime (at
/// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync(System.Threading.CancellationToken)"/>);
/// no thread-safety beyond construction-time writes is required.
/// </para>
/// </remarks>
public interface IMockVendorEnvVarRegistry
{
    /// <summary>
    /// Records a <c>(contractType → envVarKey)</c> mapping. Called by
    /// <see cref="VendorProviderServiceCollectionExtensions.UseVendorProviderIfConfigured{TContract, TReal}(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>
    /// at composition-root call time. Subsequent registrations for the
    /// same <paramref name="contractType"/> overwrite (last-writer-wins).
    /// </summary>
    void Register(Type contractType, string envVarKey);

    /// <summary>
    /// Resolves the expected real-adapter env-var key for a Tier-2 contract
    /// type, if one was recorded via <see cref="Register"/>. Returns
    /// <see langword="true"/> when a mapping exists.
    /// </summary>
    bool TryGet(Type contractType, out string envVarKey);

    /// <summary>
    /// Enumerates every recorded <c>(contractType, envVarKey)</c> pair, in
    /// registration order. Used primarily by tests to assert the registry
    /// captured the expected mappings.
    /// </summary>
    IReadOnlyList<(Type ContractType, string EnvVarKey)> Entries { get; }
}
