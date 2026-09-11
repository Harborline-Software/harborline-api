using System.Reflection;

using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.LocalNodeHost.Data.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 151 slice 2 (judge), narrowed by ticket 366 slice 1 — the bypass fence's discovery and its
/// allow-list, now covering only what the type system cannot.
/// <para>
/// RW-9 on the RECORD seam is held by <see cref="ValidatedRecordBody"/>: that seam takes a token only its
/// own mint can produce, so a record write that skips the gate or the validator does not compile and needs
/// no row here. What remains is the store's RAW (internal) seam, reachable only by the assemblies that own
/// a NON-record write shape. Row 6 of <c>RecordWriteValidationJudgeTests</c> is therefore: every raw-seam
/// call site is one of the named envelope writers below, and the exception list is empty. It lives in this
/// namespace so <see cref="AllowListVacuityArchTests"/>'s by-type coverage scan sees it.
/// </para>
/// </summary>
internal static class RecordWriteValidatedWriterFence
{
    /// <summary>
    /// The writers permitted to reach the entity store's RAW create/update seam (path prefixes of the call
    /// site). Every row is a <b>non-record envelope writer</b> — an RW-8 shape. The body is a definition
    /// envelope or a form instance, not a record, so the RECORD validator deliberately does not run on
    /// them; each keeps its own admission, named per row. A record writer can no longer be added to this
    /// block even by mistake: ticket 366 slice 1 made the raw seam internal to exactly these assemblies,
    /// and the record writers (NodeEntityWriter, NodeHierarchyCompositeCoordinator) now reach the token
    /// seam instead — their two rows were deleted with that change because the type makes those call sites
    /// impossible.
    /// </summary>
    internal static readonly string[] ValidatedWriters =
    [
        // Not a write of its own: the port's internal `CreateBatchAsync` default interface member fans out
        // to the internal `CreateAsync`. It has no production caller at all now that the record writers use
        // the token seam, so the body it would forward could only come from an envelope writer.
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

    /// <summary>
    /// The store's RAW seam only (ticket 366 slice 1): a create/update whose body is a
    /// <see cref="System.Text.Json.JsonDocument"/>. The token overloads are excluded because
    /// <see cref="ValidatedRecordBody"/> already proves what a row here would assert.
    /// </summary>
    private static bool IsRecordWrite(MethodBase target)
    {
        if (target is not MethodInfo method || method.DeclaringType is not { } declaring) return false;
        if (method.Name is not (nameof(IEntityMutationStore.CreateAsync)
            or nameof(IEntityMutationStore.UpdateAsync)
)) return false;
        if (!method.GetParameters().Any(p => p.ParameterType == typeof(System.Text.Json.JsonDocument)))
            return false;
        return declaring == typeof(IEntityMutationStore)
            || typeof(IEntityMutationStore).IsAssignableFrom(declaring);
    }
}
