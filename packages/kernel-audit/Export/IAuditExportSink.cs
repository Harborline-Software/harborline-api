using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Kernel.Audit.Export;

/// <summary>
/// The AUDIT-EXPORT seam (enrollment Phase C SoD compensating control #2; <c>project_sod_compensating_controls</c>)
/// — routes the immutable audit trail to an INDEPENDENT reviewer, bypassing the sole operator. This is the
/// "Direct Bank Delivery" control: a lone operator cannot be their own second set of eyes, so the platform
/// DELIVERS the records to an outside CPA / advisor / SIEM whose copy the operator cannot quietly alter.
/// </summary>
/// <remarks>
/// <para>
/// <b>It IS the audit-stream of the Telemetry &amp; Observability Export category</b> (CIC 2026-06-20,
/// <c>project_one_product_provider_swap_strategy</c>). Same category-provider doctrine as the rest of the
/// one-product fleet: a <b>capable bundled default</b> behind a <b>provider-swap seam</b>. The bundled default
/// (<see cref="NullAuditExportSink"/> / a local-file writer at the host) keeps the export sovereign and
/// dependency-free; an external SIEM / reviewer endpoint is the SWAP — re-pointed by a holder of
/// the audit-export policy. Triggering an export is independently authorized.
/// </para>
/// <para>
/// <b>The audit-stream is held TIGHTER than operational telemetry</b> (CIC 2026-06-20). Operational telemetry
/// (metrics, traces, logs) can flow to Harborline Toolbox's local OTel collector on a loose default; the AUDIT-stream — who
/// admitted whom, who posted to the ledger — is the compliance-grade, reviewer-facing record and rides a
/// separate, tighter-gated path (its own export sink and the
/// export act is ITSELF audited via <c>AuditEventType.AuditExported</c>).
/// </para>
/// <para>
/// <b>Never a hard dependency.</b> Export is fire-and-forget from the node's perspective — the local audit
/// trail is authoritative and complete on its own; the export is an additional delivery, not a precondition.
/// A reachability fault on the reviewer endpoint MUST NOT block enrollment, posting, or any node operation.
/// </para>
/// </remarks>
public interface IAuditExportSink
{
    /// <summary>
    /// A non-secret label identifying this export destination (the reviewer/SIEM name or endpoint label —
    /// never credentials). Recorded in the <c>AuditExported</c> audit event so the trail shows WHERE the
    /// audit was delivered.
    /// </summary>
    string DestinationLabel { get; }

    /// <summary>
    /// Deliver a batch of audit records to the independent reviewer / SIEM. Returns the count actually
    /// accepted by the destination. Implementations MUST treat the input as read-only and MUST NOT mutate the
    /// records. Fire-and-forget from the node's perspective — a transport fault should be surfaced via the
    /// implementation's own telemetry and NOT propagated as a node-blocking exception.
    /// </summary>
    /// <param name="records">The audit records to export (already tenant-scoped by the caller).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of records the destination accepted.</returns>
    ValueTask<int> ExportAsync(IReadOnlyList<AuditRecord> records, CancellationToken ct = default);
}

/// <summary>
/// No-op <see cref="IAuditExportSink"/> — the bundled, sovereign default for a host that has not configured an
/// external reviewer/SIEM. It "accepts" the records (returns the count) without sending them anywhere, so the
/// export-orchestration path (count + the <c>AuditExported</c> audit emission) is exercised identically whether
/// or not an external destination is wired. A production deployment SWAPS this for a local-file writer or an
/// external SIEM/reviewer adapter (the category-provider swap).
/// </summary>
public sealed class NullAuditExportSink : IAuditExportSink
{
    /// <summary>The shared singleton no-op instance.</summary>
    public static readonly NullAuditExportSink Instance = new();

    /// <inheritdoc />
    public string DestinationLabel => "none (bundled no-op)";

    /// <inheritdoc />
    public ValueTask<int> ExportAsync(IReadOnlyList<AuditRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        // Sovereign default: the local trail is authoritative; "exporting" to nowhere accepts all records so
        // the orchestration path is uniform across wired / un-wired hosts.
        return new ValueTask<int>(records.Count);
    }
}
