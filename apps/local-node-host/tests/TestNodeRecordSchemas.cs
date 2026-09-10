using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Schema.DependencyInjection;

// Test assembly only: the node's record-schema registrations over a throwaway registry.
namespace Harborline.Api.LocalNodeHost.Data.Entities;

internal static class TestNodeRecordSchemas
{
    /// <summary>The node's record schemas over a fresh in-memory registry.</summary>
    internal static NodeRecordSchemas Fresh() => new(NewRegistry());

    /// <summary>The node's record schemas over a registry the test also inspects.</summary>
    internal static NodeRecordSchemas Over(ISchemaRegistry registry) => new(registry);

    internal static ISchemaRegistry NewRegistry() => new ServiceCollection()
        .AddSingleton(TimeProvider.System)
        .AddHarborlineKernelSchemaRegistry()
        .BuildServiceProvider()
        .GetRequiredService<ISchemaRegistry>();
}
