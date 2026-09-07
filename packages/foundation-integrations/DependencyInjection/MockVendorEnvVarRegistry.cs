namespace Harborline.Api.Foundation.Integrations.DependencyInjection;

/// <summary>
/// Default <see cref="IMockVendorEnvVarRegistry"/> backed by a thin
/// <see cref="Dictionary{TKey, TValue}"/> wrapper. Per ADR 0096 §D1c
/// the registry is a singleton populated at composition-root call time and
/// queried once at <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync(System.Threading.CancellationToken)"/>;
/// no concurrency discipline beyond construction-time writes is required.
/// </summary>
public sealed class MockVendorEnvVarRegistry : IMockVendorEnvVarRegistry
{
    private readonly Dictionary<Type, string> _byContractType = new();
    private readonly List<(Type ContractType, string EnvVarKey)> _registrationOrder = new();

    /// <inheritdoc />
    public void Register(Type contractType, string envVarKey)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentException.ThrowIfNullOrWhiteSpace(envVarKey);

        if (_byContractType.ContainsKey(contractType))
        {
            // Last-writer-wins on overwrite — still record the registration in
            // the order list so tests can observe the duplicate registration
            // and so an operator survey of the registry sees the most recent
            // env-var name an operator was supposed to set.
            _byContractType[contractType] = envVarKey;
            _registrationOrder.Add((contractType, envVarKey));
            return;
        }

        _byContractType[contractType] = envVarKey;
        _registrationOrder.Add((contractType, envVarKey));
    }

    /// <inheritdoc />
    public bool TryGet(Type contractType, out string envVarKey)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        if (_byContractType.TryGetValue(contractType, out var value))
        {
            envVarKey = value;
            return true;
        }

        envVarKey = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<(Type ContractType, string EnvVarKey)> Entries => _registrationOrder;
}
