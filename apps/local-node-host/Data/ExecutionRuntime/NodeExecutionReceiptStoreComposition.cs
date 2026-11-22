using Harborline.Foundation.ExecutionRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Data.ExecutionRuntime;

/// <summary>Composes the node adapter for the platform's execution receipt port.</summary>
public static class NodeExecutionReceiptStoreComposition
{
    /// <summary>Registers the receipt store; the caller supplies the node context factory and module catalogue.</summary>
    public static IServiceCollection AddNodeExecutionReceiptStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExecutionReceiptStore, NodeExecutionReceiptStore>();
        return services;
    }
}
