using System.Reflection;

using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.LocalNodeHost.Data.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 151 slice 2 (judge) — the bypass fence's discovery and its allow-list.
/// <para>
/// Every production call of the entity store's create/update ports is a record write. The property row 6 of
/// <c>RecordWriteValidationJudgeTests</c> states is that each such call site sits inside a NAMED validated
/// writer — a type the slice has shown runs the gate and then the validator before persistence — and that
/// the exception list is empty. It lives in this namespace so
/// <see cref="AllowListVacuityArchTests"/>'s by-type coverage scan sees it.
/// </para>
/// </summary>
internal static class RecordWriteValidatedWriterFence
{
    /// <summary>
    /// The writers permitted to reach the entity store's create/update ports (path prefixes of the call
    /// site). Two labelled kinds, and every row carries the reason it is here:
    /// <list type="bullet">
    ///   <item><b>Record writers</b> — gated (RW-1) then validated by the record validator (RW-2) before
    ///     persistence. RW-9's "a writer that skips RW-1 or RW-2 fails it" bites on these.</item>
    ///   <item><b>Non-record envelope writers</b> — the RW-8 shapes. The body is a definition envelope or a
    ///     form instance, not a record, so the RECORD validator deliberately does not run on them; each
    ///     keeps its own admission, named per row. A record writer must never be added to this block.</item>
    /// </list>
    /// </summary>
    internal static readonly string[] ValidatedWriters =
    [
        // ── Record writers: gate → record validator → persistence ──
        // Route and headless-CLI create/update. Validates in NodeEntityWriter.ValidateAsync, after the gate.
        "apps/local-node-host/Data/Entities/NodeEntityWriter.cs",
        // Hierarchy split/merge mints records; validates each minted body after the composite admission.
        "apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs",

        // ── Non-record envelope writers (RW-8): the record validator deliberately does not run ──
        // Not a write of its own: the port's `CreateBatchAsync` default interface member fans out to
        // `CreateAsync`, so the body it forwards was already admitted by whichever row above called it.
        "packages/foundation/Assets/Entities/IEntityStore.cs",
        // Body is a serialized DEFINITION envelope on a lifecycle status transition; admitted by the
        // definition lifecycle's own allowed-from transition guard, which a record schema cannot express.
        "packages/foundation/Definitions/EntityStoreDefinitionLifecycle.cs",
        // Body is a serialized WORKFLOW DEFINITION envelope (EntityStoreWorkflowDefinitionStore
        // .SerializeEnvelope) under its own conflict + authorization admission; not a record body.
        "packages/blocks-workflow/src/durable/AuthorizedWorkflowDefinitionLifecycle.cs",
        // Body is a serialized FORM DEFINITION envelope, admitted by the forms definition path (frozen
        // definition + lineage checks, FormDefinitionValidationException); form INSTANCES are validated
        // against their own form schema by that path, never by the record validator.
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
            or nameof(IEntityMutationStore.UpdateAsync)
)) return false;
        return declaring == typeof(IEntityMutationStore)
            || typeof(IEntityMutationStore).IsAssignableFrom(declaring);
    }
}
