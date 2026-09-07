namespace Harborline.Api.Foundation.Integrations.DependencyInjection;

/// <summary>
/// Thrown by <see cref="MockProviderProductionGuardAssertion"/> at
/// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync(System.Threading.CancellationToken)"/>
/// when <c>ASPNETCORE_ENVIRONMENT=Production</c> AND one or more Tier-2
/// contracts are registered with a concrete that implements
/// <see cref="IMockVendorProvider"/>, AND neither the per-contract
/// real-adapter env var is present, nor the global opt-out env var
/// <c>HARBORLINE_ALLOW_MOCK_PROVIDERS</c> parses to <c>true</c> via
/// <see cref="bool.TryParse(string, out bool)"/>.
/// </summary>
/// <remarks>
/// The exception carries the full typed failure list — one entry per Tier-2
/// contract that failed the production-safety check. The message format
/// names both the contract type AND the expected env-var key per entry,
/// closing the silent-typo foot-gun by returning the canonical key alongside
/// the affected contract.
/// </remarks>
public sealed class MockInProductionException : InvalidOperationException
{
    /// <summary>
    /// The list of <c>(contractType, envVarKey)</c> tuples that failed the
    /// production-safety check, in <see cref="Microsoft.Extensions.DependencyInjection.ServiceDescriptor"/>
    /// iteration order.
    /// </summary>
    public IReadOnlyList<(Type ContractType, string EnvVarKey)> Failures { get; }

    /// <summary>
    /// Constructs a <see cref="MockInProductionException"/> from the typed
    /// failure list. The exception message enumerates each
    /// <c>(TContract, envVarKey)</c> tuple.
    /// </summary>
    public MockInProductionException(IReadOnlyList<(Type ContractType, string EnvVarKey)> failures)
        : base(BuildMessage(failures))
    {
        Failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    private static string BuildMessage(IReadOnlyList<(Type ContractType, string EnvVarKey)> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var enumeration = string.Join(
            "; ",
            failures.Select(f => $"{f.ContractType.Name} expected {f.EnvVarKey}"));

        return
            "Production-environment mock providers detected without opt-out. "
            + "The following Tier-2 contracts are registered with mock concrete types "
            + "but neither the corresponding real-adapter environment variable is set "
            + "nor HARBORLINE_ALLOW_MOCK_PROVIDERS=true: "
            + enumeration
            + ". Either set the listed environment variable(s) so the real adapter swap "
            + "fires, or set HARBORLINE_ALLOW_MOCK_PROVIDERS=true to explicitly opt-in to "
            + "running with mocks (load-test environments, closed demo deployments, "
            + "on-prem trials before real-vendor accounts are provisioned). "
            + "Per ADR 0096 §D1c.";
    }
}
