using System.Reflection;

using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.LocalNodeHost.Data.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 151 slice 2 — every production entity-store create or update is held at a named writer that
/// validates after its gate. The judge test consumes this inventory directly.
/// </summary>
internal static class RecordWriteValidatedWriterFence
{
    /// <summary>The writers whose record writes are gated then validated (path prefixes of the call site).</summary>
    internal static readonly string[] ValidatedWriters =
    [
        "apps/local-node-host/Data/Entities/NodeEntityWriter.cs",
        "apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs",
        "packages/foundation/Assets/Entities/IEntityStore.cs",
        "packages/foundation/Definitions/EntityStoreDefinitionLifecycle.cs",
        "packages/blocks-workflow/src/durable/EntityStoreWorkflowDefinitionStore.cs",
        "packages/blocks-workflow/src/durable/AuthorizedWorkflowDefinitionLifecycle.cs",
        "packages/foundation-forms/EntityStoreFormDefinitionStore.cs",
        "packages/foundation-forms/AuthorizedFormDefinitionLifecycle.cs",
    ];

    /// <summary>Rule 12: the per-call-site exception list, held empty. A row here needs a named reason.</summary>
    internal static readonly string[] ExceptionRows = [];

    /// <summary>Every production call site of <see cref="IEntityMutationStore"/> create/update.</summary>
    internal static string[] DiscoveredRecordWriteCallers() =>
        RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [
                typeof(IEntityMutationStore).Assembly,
                typeof(NodeEntityWriter).Assembly,
                typeof(Harborline.Api.Foundation.Forms.AuthorizedFormDefinitionLifecycle).Assembly,
                typeof(Harborline.Api.Blocks.Workflow.Durable.AuthorizedWorkflowDefinitionLifecycle).Assembly,
            ],
            IsRecordWrite)
        .Select(site => $"{site.Path}|{site.Symbol}")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsRecordWrite(MethodBase target)
    {
        if (target is not MethodInfo method || method.DeclaringType is not { } declaring) return false;
        if (method.Name is not (nameof(IEntityMutationStore.CreateAsync)
            or nameof(IEntityMutationStore.UpdateAsync))) return false;
        return declaring == typeof(IEntityMutationStore)
            || typeof(IEntityMutationStore).IsAssignableFrom(declaring);
    }
}
