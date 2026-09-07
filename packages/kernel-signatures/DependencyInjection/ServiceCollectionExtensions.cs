using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Kernel.Signatures.Services;

namespace Harborline.Api.Kernel.Signatures.DependencyInjection;

/// <summary>DI registrations for kernel-signatures (W#21 Phase 1).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory implementations of
    /// <see cref="IConsentRegistry"/>, <see cref="ISignatureCapture"/>,
    /// and <see cref="ISignatureRevocationLog"/> as singletons. Suitable
    /// for tests, demos, and single-process kitchen-sink scenarios.
    /// Production hosts override with persistence-backed implementations.
    /// </summary>
    public static IServiceCollection AddInMemoryKernelSignatures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IConsentRegistry, InMemoryConsentRegistry>();
        services.AddSingleton<ISignatureCapture, InMemorySignatureCapture>();
        services.AddSingleton<ISignatureRevocationLog, InMemorySignatureRevocationLog>();
        return services;
    }
}
