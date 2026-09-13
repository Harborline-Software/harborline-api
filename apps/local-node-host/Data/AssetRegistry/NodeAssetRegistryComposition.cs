using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Foundation.Forms.Engine.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// Wires the Asset Type System LIVE into the node host (ADR 0101 Rev 3.1 Wave 2b): the registry stores
/// on the durable foundation audit substrate (U1), the condition-capture projector, and — critically —
/// the forms <c>ProjectingFormEngine</c> decoration, so submitting a condition-rating inspection form
/// through the node's forms submit route captures the entity's typed condition assessment
/// ("one act, two artifacts") going forward.
/// </summary>
/// <remarks>
/// <para>
/// <b>Must be called AFTER <c>AddNodeForms()</c></b>: it decorates the registered
/// <c>IFormEngine</c> (so a form submit fires the projections) and rides the foundation
/// <c>Harborline.Api.Foundation.Assets.Audit.IAuditLog</c> that the forms composition already registered
/// (via <c>AddHarborlineAssetsInMemory</c>).
/// </para>
/// <para>
/// <b>Gate posture (Wave 2b).</b> Live capture is only wired here because the F-ATOM (at-least-once
/// outbox), F-SKIP (audited skips), and F-CLOCK (engine submit clock) gates are closed — the
/// projection runner the decoration invokes is the durable <c>OutboxFormSubmitProjectionRunner</c>, so
/// a committed submission whose projection is interrupted is recoverable, not silently lost.
/// </para>
/// </remarks>
public static class NodeAssetRegistryComposition
{
    /// <summary>
    /// Composes the in-memory Asset-Type-System stores, swaps their X-AUDIT journal onto the durable
    /// foundation audit substrate (U1), registers the condition-capture projection, decorates the
    /// forms engine so a submission fires the registered post-submit projections, and — critically —
    /// schedules the reconcile-sweep daemon that DRAINS the projection outbox on the running node
    /// (F-RECON). Structural durability (the outbox) and operational recovery (the daemon) ship together:
    /// wiring live capture without the sweep would leave interrupted projections recoverable only by a
    /// client retry (the double-submit path F-ROUTE closes).
    /// </summary>
    /// <param name="reconcileSweepInterval">
    /// The periodic reconcile-sweep interval. Null ⇒ <see cref="FormSubmitProjectionReconcilerDaemon.DefaultInterval"/>
    /// (60s). The startup drain (the first sweep) runs immediately regardless of this value.
    /// </param>
    public static IServiceCollection AddNodeAssetRegistry(
        this IServiceCollection services, TimeSpan? reconcileSweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddInMemoryAssetTypeSystem()        // Wave-1 stores (entities, edges, types, conditions, scoring)
            .AddDurableAssetRegistryAudit()      // U1 — ride the foundation IAuditLog the node already has
            .AddConditionCaptureProjection()     // the condition-rating field-kind projector + binding store
            .AddFormSubmissionRecordProjection(); // #144 — the generic submission → record-link projector

        // Fire the registered projections on every successful submit (idempotent — no double-wrap). This
        // also registers the durable outbox + IFormSubmitProjectionReconciler (via AddFormSubmitProjections).
        services.AddFormSubmitProjectionDecoration();

        // F-RECON: schedule the recovery half — a startup drain + periodic sweep of the projection outbox,
        // so an interrupted projection heals automatically on the node instead of only via a client retry.
        services.AddHostedService<FormSubmitProjectionReconcilerDaemon>(sp => new FormSubmitProjectionReconcilerDaemon(
            sp.GetRequiredService<Harborline.Api.Foundation.Forms.Submission.IFormSubmitProjectionReconciler>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FormSubmitProjectionReconcilerDaemon>>(),
            reconcileSweepInterval));

        return services;
    }
}
